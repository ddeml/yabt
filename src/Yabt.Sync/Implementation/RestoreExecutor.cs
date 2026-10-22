using System.Buffers;
using System.IO.Hashing;
using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class RestoreExecutor
(
    ILogger<RestoreExecutor> _logger,
    TimeProvider _timeProvider,
    ArchiveRootDocuments _rootDocuments,
    ArchiveManifestStore _manifests
)
{
    private async Task<IObjectStoreStagingArea> CreateRestoreStagingDirectoryAsync
    (
        IRestoreDestination restoreTarget,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(CreateRestoreStagingDirectoryAsync));

        try
        {
            return await restoreTarget.CreateStagingAreaAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException("The restore staging area could not be created.", ex);
        }
    }

    private async Task<PreparedRestoreFile> PrepareRestoreFileAsync
    (
        ArchiveProjectedObject file,
        IObjectStoreStagingArea? stagingDirectory,
        bool stageForWrite,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(PrepareRestoreFileAsync));

        string? stagingToken = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.ReportObjectReadOperation(
                file.RelativePath,
                stageForWrite ? "restore staging" : "restore validation");
            ArchiveObjectContent projectedContent;
            try
            {
                projectedContent = await file.OpenContentAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ex is OperationCanceledException) { throw; }

                throw new RestoreItemReadException(file.RelativePath, ex);
            }

            await using (projectedContent)
            {
                if (!stageForWrite)
                {
                    await CopyRestoreContentToStagingAsync(
                        file,
                        projectedContent.Content,
                        Stream.Null,
                        cancellationToken);
                    return new(file, stagingToken: null, stagingDirectory, _logger);
                }

                stagingToken = (stagingDirectory ??
                    throw new YabtSyncException(
                        "A restore file write requires a staging directory."))
                    .CreateFileToken();
                _logger.LogControlMetadataOperation(
                    "Creating restore staging file",
                    stagingToken);
                await using (var stagedContent = stagingDirectory!.OpenWrite(stagingToken))
                {
                    await CopyRestoreContentToStagingAsync(
                        file,
                        projectedContent.Content,
                        stagedContent,
                        cancellationToken);
                    await stagedContent.FlushAsync(cancellationToken);
                }
            }

            _logger.LogControlMetadataOperation(
                "Finished writing restore staging file",
                stagingToken);
            var preparedFile = new PreparedRestoreFile(file, stagingToken, stagingDirectory, _logger);
            stagingToken = null;
            return preparedFile;
        }
        catch (Exception ex)
        {
            if (stagingToken is not null)
            {
                PreparedRestoreFile.TryDeleteRestoreTemporaryPath(
                    _logger,
                    stagingToken,
                    () => stagingDirectory!.DeleteFile(stagingToken));
            }

            if (ex is YabtSyncException) { throw; }

            var action = stageForWrite ? "staged" : "validated byte-for-byte";
            throw new YabtSyncException(
                $"Archive object for restore path '{file.RelativePath}' could not be {action}.",
                ex);
        }
    }

    private static async Task CopyRestoreContentToStagingAsync
    (
        ArchiveProjectedObject file,
        Stream source,
        Stream destination,
        CancellationToken cancellationToken
    )
    {
        var hash = new XxHash128();
        long contentLength = 0;
        var buffer = new byte[ArchiveContentTransfer.DefaultBufferSize];
        while (true)
        {
            int bytesRead;
            try
            {
                bytesRead = await source.ReadAsync(buffer, cancellationToken);
            }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ex is OperationCanceledException) { throw; }

                throw new RestoreItemReadException(file.RelativePath, ex);
            }

            if (bytesRead == 0) { break; }

            hash.Append(buffer.AsSpan(0, bytesRead));
            contentLength += bytesRead;
            await destination.WriteAsync(
                buffer.AsMemory(0, bytesRead),
                cancellationToken);
        }

        if (file.ContentLength.HasValue && file.ContentLength.Value != contentLength)
        {
            throw new RestoreItemReadException(
                file.RelativePath,
                $"Archive object for restore path '{file.RelativePath}' has length {contentLength}, " +
                    $"but {file.ContentLength.Value} bytes were expected.");
        }

        var contentHash = ArchiveHash.Format(hash.GetHashAndReset());
        if (!string.Equals(file.ContentHash, contentHash, StringComparison.Ordinal))
        {
            throw new RestoreItemReadException(
                file.RelativePath,
                $"Archive object for restore path '{file.RelativePath}' failed its content hash check.");
        }
    }

    internal async Task<IReadOnlyList<SyncItemFailure>> ApplyRestorePlanAsync
    (
        IArchiveMutableObjectStore destinationStore,
        ArchiveLayout destinationLayout,
        RestorePlan plan,
        RestoreReconciliation reconciliation,
        IRestoreDestination restoreTarget,
        LogicalStateManifestLoad logicalStateManifestLoad,
        RootDescriptorRestore rootDescriptorRestore,
        BackupRootDocument? archiveRootDocument,
        bool restoreRootNeedsCreation,
        bool byteForByte,
        int restoreMaximumConcurrency,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(ApplyRestorePlanAsync));

        var failures = new List<SyncItemFailure>(reconciliation.Failures);
        var liveStateChanged = reconciliation.NewCount != 0 ||
            reconciliation.ChangedCount != 0 ||
            reconciliation.ExtraCount != 0;
        var changeManifestInvalidationMarkerActive = false;
        var logicalStateManifestInvalidationMarkerActive =
            logicalStateManifestLoad.InvalidationMarkerExists;
        ArchiveHistorizer? historizer = null;
        var mutationStarted = false;
        var pendingHistoryMoves = reconciliation.HistoryMoves.ToHashSet();

        async Task EnsureMutationStartedAsync(CancellationToken currentCancellationToken)
        {
            if (mutationStarted) { return; }

            changeManifestInvalidationMarkerActive =
                await _manifests.PrepareRestoreChangeManifestMutationAsync(
                    destinationStore,
                    liveStateChanged || rootDescriptorRestore.NeedsWrite,
                    currentCancellationToken);
            if (liveStateChanged &&
                logicalStateManifestLoad.ManifestExists &&
                !logicalStateManifestInvalidationMarkerActive)
            {
                await _manifests.UploadLogicalStateManifestInvalidationMarkerAsync(
                    destinationStore,
                    currentCancellationToken);
                logicalStateManifestInvalidationMarkerActive = true;
            }

            historizer = new ArchiveHistorizer(
                destinationStore,
                destinationLayout,
                _timeProvider.GetUtcNow(),
                _logger);
            await historizer.InspectRecoveryStateAsync(currentCancellationToken);
            mutationStarted = true;
        }

        async Task MoveHistoryItemAsync
        (
            RestoreHistoryMove historyMove,
            CancellationToken currentCancellationToken
        )
        {
            var activeHistorizer = historizer ??
                throw new YabtSyncException(
                    "Restore historization was requested before mutation initialization.");
            if (historyMove.IsFolder)
            {
                await activeHistorizer.MoveFolderAsync(
                    historyMove.RelativePath,
                    "Restore",
                    currentCancellationToken);
            }
            else
            {
                await activeHistorizer.MoveObjectAsync(
                    historyMove.RelativePath,
                    "Restore",
                    currentCancellationToken);
            }

            _logger.ReportRestoreItemHistorized(historyMove);
        }

        async Task BeforeRestoreFileCommitAsync
        (
            ArchiveProjectedObject file,
            CancellationToken currentCancellationToken
        )
        {
            await EnsureMutationStartedAsync(currentCancellationToken);
            foreach (var historyMove in reconciliation.HistoryMoves)
            {
                if (!pendingHistoryMoves.Contains(historyMove) ||
                    !RestoreHistoryMoveBlocksFile(historyMove, file.RelativePath, plan.Namespace.PathComparer))
                {
                    continue;
                }

                await MoveHistoryItemAsync(historyMove, currentCancellationToken);
                pendingHistoryMoves.Remove(historyMove);
            }
        }

        failures.AddRange(await ApplyRestoreFilesAsync(
            plan,
            reconciliation,
            destinationLayout,
            restoreTarget,
            byteForByte,
            restoreMaximumConcurrency,
            BeforeRestoreFileCommitAsync,
            cancellationToken));

        if (failures.Count != 0)
        {
            return failures;
        }

        await EnsureMutationStartedAsync(cancellationToken);
        foreach (var historyMove in reconciliation.HistoryMoves)
        {
            if (!pendingHistoryMoves.Remove(historyMove)) { continue; }

            await MoveHistoryItemAsync(historyMove, cancellationToken);
        }

        await restoreTarget.CreateDirectoryAsync(
            destinationLayout.ToLiveObjectKey(string.Empty),
            cancellationToken);
        if (restoreRootNeedsCreation)
        {
            _logger.ReportRestoreItemWritten("new", "directory", ".");
        }
        foreach (var directoryPath in plan.EmptyDirectories)
        {
            await restoreTarget.CreateDirectoryAsync(
                destinationLayout.ToLiveObjectKey(directoryPath),
                cancellationToken);
            if (reconciliation.NewDesiredPaths.Contains(directoryPath) ||
                reconciliation.ChangedDesiredPaths.Contains(directoryPath))
            {
                _logger.ReportRestoreItemWritten(
                    ArchiveOperationLogging.GetRestoreChangeKind(reconciliation, directoryPath),
                    "directory",
                    directoryPath);
            }
        }

        foreach (var directoryPath in reconciliation.ChangedDesiredPaths
                     .Where(path => plan.Directories.Contains(path, plan.Namespace.PathComparer) &&
                         !plan.EmptyDirectories.Contains(path, plan.Namespace.PathComparer)))
        {
            _logger.ReportRestoreItemWritten("changed", "directory", directoryPath);
        }

        if (rootDescriptorRestore.NeedsWrite)
        {
            if (archiveRootDocument is null)
            {
                throw new YabtSyncException(
                    "Restore planned a root descriptor write without preserved archive bytes.");
            }

            if (rootDescriptorRestore.MoveExistingToHistory)
            {
                await (historizer ??
                    throw new YabtSyncException(
                        "Restore root descriptor historization was not initialized."))
                    .MoveRootObjectAsync(
                    BackupRootFileNames.Primary,
                    "Restore",
                    cancellationToken);
                _logger.LogControlMetadataOperation(
                    "Moved to restore history",
                    BackupRootFileNames.Primary);
            }

            await _rootDocuments.UploadRootDocumentAsync(
                destinationStore,
                archiveRootDocument,
                cancellationToken);
        }

        await (historizer ??
            throw new YabtSyncException(
                "Restore mutation did not initialize historization."))
            .CompleteAsync(cancellationToken);

        using var nextLogicalStateManifest = await _manifests.CreateLogicalStateManifestAsync
        (
            destinationStore,
            destinationLayout,
            plan,
            cancellationToken
        );
        var logicalStateManifestNeedsWrite =
            logicalStateManifestInvalidationMarkerActive ||
            logicalStateManifestLoad.Manifest is null ||
            !string.Equals(
                logicalStateManifestLoad.Manifest.ManifestHash,
                nextLogicalStateManifest.ManifestHash,
                StringComparison.Ordinal);
        if (logicalStateManifestNeedsWrite)
        {
            if (logicalStateManifestLoad.ManifestExists &&
                !logicalStateManifestInvalidationMarkerActive)
            {
                await _manifests.UploadLogicalStateManifestInvalidationMarkerAsync(
                    destinationStore,
                    cancellationToken);
                logicalStateManifestInvalidationMarkerActive = true;
            }

            if (logicalStateManifestLoad.ManifestExists)
            {
                await _manifests.DeleteInternalObjectAsync(
                    destinationStore,
                    ArchiveLogicalStateManifest.FileName,
                    "Obsolete logical state manifest",
                    cancellationToken);
            }

            await _manifests.UploadLogicalStateManifestAsync(
                destinationStore,
                nextLogicalStateManifest,
                cancellationToken);
        }

        if (logicalStateManifestInvalidationMarkerActive)
        {
            await _manifests.DeleteInternalObjectAsync(
                destinationStore,
                ArchiveLogicalStateManifest.InvalidationMarkerFileName,
                "Logical state manifest invalidation marker",
                cancellationToken);
        }

        if (changeManifestInvalidationMarkerActive)
        {
            await _manifests.DeleteInternalObjectAsync(
                destinationStore,
                ArchiveChangeManifest.InvalidationMarkerFileName,
                "Change manifest invalidation marker",
                cancellationToken);
        }

        foreach (var relativePath in reconciliation.UnchangedDesiredPaths)
        {
            _logger.ReportRestoreItemUnchanged(
                ArchiveOperationLogging.GetRestoreItemKind(plan, relativePath),
                relativePath);
        }

        return failures;
    }

    private async Task<IReadOnlyList<SyncItemFailure>> ApplyRestoreFilesAsync
    (
        RestorePlan plan,
        RestoreReconciliation reconciliation,
        ArchiveLayout destinationLayout,
        IRestoreDestination restoreTarget,
        bool byteForByte,
        int restoreMaximumConcurrency,
        Func<ArchiveProjectedObject, CancellationToken, Task> beforeFileCommitAsync,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(ApplyRestoreFilesAsync));

        var pathComparer = plan.Namespace.PathComparer;
        var alreadyFailedPaths = reconciliation.Failures
            .Select(failure => failure.RelativePath)
            .ToHashSet(pathComparer);
        var filesToWrite = reconciliation.FilesToWrite
            .Select(file => file.RelativePath)
            .ToHashSet(pathComparer);
        IReadOnlyList<ArchiveProjectedObject> filesToPrepare = byteForByte ?
            plan.Files
                .Where(file => !alreadyFailedPaths.Contains(file.RelativePath))
                .ToArray() :
            reconciliation.FilesToWrite;
        var projectionUsage = await RestoreProjectionUsage.CreateAsync(
            plan,
            filesToPrepare);
        if (filesToPrepare.Count == 0) { return []; }

        var failures = new List<SyncItemFailure>();

        IObjectStoreStagingArea? stagingDirectory = null;
        if (filesToWrite.Count != 0)
        {
            stagingDirectory = await CreateRestoreStagingDirectoryAsync(
                restoreTarget,
                cancellationToken);
        }

        try
        {
            using var workerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            var workerToken = workerCancellation.Token;
            var pendingFiles = new Dictionary<
                Task<PreparedRestoreFile>,
                ArchiveProjectedObject>();
            var nextFileIndex = 0;

            void FillPipeline()
            {
                while (pendingFiles.Count < restoreMaximumConcurrency &&
                    nextFileIndex < filesToPrepare.Count)
                {
                    var file = filesToPrepare[nextFileIndex++];
                    pendingFiles.Add(PrepareRestoreFileAsync(
                        file,
                        stagingDirectory,
                        filesToWrite.Contains(file.RelativePath),
                        workerToken),
                        file);
                }
            }

            FillPipeline();
            try
            {
                while (pendingFiles.Count != 0)
                {
                    var pendingFile = await Task.WhenAny(pendingFiles.Keys);
                    var file = pendingFiles[pendingFile];
                    pendingFiles.Remove(pendingFile);
                    PreparedRestoreFile preparedFile;
                    try
                    {
                        preparedFile = await pendingFile;
                    }
                    catch (RestoreItemReadException ex)
                    {
                        failures.Add(_logger.CreateReadFailure(
                            file.RelativePath,
                            "restore archive read",
                            ex));
                        await projectionUsage.ReleaseAsync(file.RelativePath);
                        FillPipeline();
                        continue;
                    }

                    try
                    {
                        await using (preparedFile)
                        {
                            if (preparedFile.StagingToken is not null)
                            {
                                await beforeFileCommitAsync(
                                    preparedFile.File,
                                    cancellationToken);
                                await restoreTarget.CommitStagedFileAsync(
                                    destinationLayout.ToLiveObjectKey(
                                        preparedFile.File.RelativePath),
                                    preparedFile.StagingToken,
                                    preparedFile.File.LastModifiedUtc,
                                    cancellationToken);
                                preparedFile.MarkCommitted();
                                _logger.ReportRestoreItemWritten(
                                    ArchiveOperationLogging.GetRestoreChangeKind(
                                        reconciliation,
                                        preparedFile.File.RelativePath),
                                    "file",
                                    preparedFile.File.RelativePath);
                            }
                        }
                    }
                    finally
                    {
                        await projectionUsage.ReleaseAsync(
                            file.RelativePath);
                    }

                    FillPipeline();
                }
            }
            finally
            {
                workerCancellation.Cancel();
                foreach (var (pendingFile, file) in pendingFiles)
                {
                    try
                    {
                        var preparedFile = await pendingFile;
                        await preparedFile.DisposeAsync();
                    }
                    catch (Exception)
                    {
                        // Preserve the operation failure while every worker observes cancellation.
                    }
                    finally
                    {
                        await projectionUsage.ReleaseAsync(file.RelativePath);
                    }
                }
            }
        }
        finally
        {
            if (stagingDirectory is not null)
            {
                await stagingDirectory.DisposeAsync();
            }
        }

        return failures;
    }

    private static bool RestoreHistoryMoveBlocksFile
    (
        RestoreHistoryMove historyMove,
        string fileRelativePath,
        StringComparer comparer
    )
    {
        var historyPath = ObjectPath.Parse(historyMove.RelativePath);
        var filePath = ObjectPath.Parse(fileRelativePath);
        if (comparer.Equals(historyPath.Value, filePath.Value)) { return true; }
        if (historyMove.IsFolder) { return false; }

        for (var parent = filePath.Parent; !parent.IsRoot; parent = parent.Parent)
        {
            if (comparer.Equals(historyPath.Value, parent.Value)) { return true; }
        }

        return false;
    }

    internal async Task<IReadOnlyList<SyncItemFailure>> ValidateRestorePlanContentAsync
    (
        RestorePlan plan,
        int restoreMaximumConcurrency,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(ValidateRestorePlanContentAsync));

        var failures = new System.Collections.Concurrent.ConcurrentBag<SyncItemFailure>();
        var projectionUsage = await RestoreProjectionUsage.CreateAsync(
            plan,
            plan.Files);
        await Parallel.ForEachAsync(
            plan.Files,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = restoreMaximumConcurrency,
            },
            async (file, currentCancellationToken) =>
            {
                try
                {
                    try
                    {
                        await using var preparedFile = await PrepareRestoreFileAsync(
                            file,
                            stagingDirectory: null,
                            stageForWrite: false,
                            currentCancellationToken);
                    }
                    catch (RestoreItemReadException ex)
                    {
                        failures.Add(_logger.CreateReadFailure(
                            file.RelativePath,
                            "restore archive validation",
                            ex));
                    }
                }
                finally
                {
                    await projectionUsage.ReleaseAsync(file.RelativePath);
                }
            });

        return SyncFailureReporting.NormalizeFailures(failures);
    }
}

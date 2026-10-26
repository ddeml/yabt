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
        var session = CreateSession(
            destinationStore, destinationLayout, restoreTarget, logicalStateManifestLoad,
            rootDescriptorRestore, archiveRootDocument);
        failures.AddRange(await ApplyRestoreFilesAsync(
            plan,
            reconciliation,
            destinationLayout,
            restoreTarget,
            byteForByte,
            restoreMaximumConcurrency,
            (file, token) => session.BeforeFileCommitAsync(reconciliation, file, token),
            cancellationToken));

        if (failures.Count != 0)
        {
            return failures;
        }

        await session.CompleteAsync(
            plan, reconciliation, restoreRootNeedsCreation, cancellationToken);

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            foreach (var relativePath in reconciliation.UnchangedDesiredPaths)
            {
                _logger.ReportRestoreItemUnchanged(
                    ArchiveOperationLogging.GetRestoreItemKind(plan, relativePath),
                    relativePath);
            }
        }

        return failures;
    }

    internal RestoreMutationSession CreateSession
    (
        IArchiveMutableObjectStore destinationStore,
        ArchiveLayout destinationLayout,
        IRestoreDestination restoreTarget,
        LogicalStateManifestLoad logicalStateManifestLoad,
        RootDescriptorRestore rootDescriptorRestore,
        BackupRootDocument? archiveRootDocument
    )
    {
        _logger.LogTrace(nameof(CreateSession));
        return new(destinationStore, destinationLayout, restoreTarget, logicalStateManifestLoad,
            rootDescriptorRestore, archiveRootDocument, _manifests, _rootDocuments,
            _timeProvider, _logger);
    }

    internal async Task<IReadOnlyList<SyncItemFailure>> ApplyRestoreFilesAsync
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

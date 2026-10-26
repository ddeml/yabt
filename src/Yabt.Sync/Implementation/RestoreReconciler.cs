using System.Buffers;
using Microsoft.Extensions.Logging;
using Yabt.Common;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class RestoreReconciler
(
    ILogger<RestoreReconciler> _logger
)
{
    internal async Task<RestoreReconciliation> CreateRestoreReconciliationAsync
    (
        IObjectStore destinationStore,
        ArchiveLayout destinationLayout,
        RestorePlan restorePlan,
        ArchiveLogicalStateManifest? logicalStateManifest,
        bool byteForByte,
        HashSet<string> blockedRestorePaths,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(CreateRestoreReconciliationAsync));

        var pathComparer = restorePlan.Namespace.PathComparer;
        var destinationState = await LoadRestoreDestinationStateAsync
        (
            destinationStore,
            destinationLayout,
            pathComparer,
            cancellationToken
        );
        using var reconciliationIndex = TemporaryIndex.Create();
        using var logicalStateEntries = reconciliationIndex.CreateDictionary<ArchiveLogicalStateManifestEntry>(
            comparer: pathComparer);
        if (logicalStateManifest is not null)
        {
            foreach (var logicalStateEntry in logicalStateManifest.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!logicalStateEntries.TryAdd(
                        logicalStateEntry.LogicalRelativePath,
                        logicalStateEntry))
                {
                    throw new YabtSyncException(
                        "Logical-state manifest paths cannot be represented distinctly on " +
                            $"this filesystem: '{logicalStateEntry.LogicalRelativePath}'.");
                }
            }
        }
        return await CreateRestoreFolderReconciliationAsync(
            destinationStore, restorePlan, destinationState, logicalStateEntries,
            byteForByte, blockedRestorePaths, cancellationToken, destinationLayout);
    }

    internal async Task<RestoreReconciliation> CreateRestoreFolderReconciliationAsync
    (
        IObjectStore destinationStore,
        RestorePlan restorePlan,
        RestoreDestinationState destinationState,
        IReadOnlyDictionary<string, ArchiveLogicalStateManifestEntry> logicalStateEntries,
        bool byteForByte,
        HashSet<string> blockedRestorePaths,
        CancellationToken cancellationToken,
        ArchiveLayout? destinationLayout = null
    )
    {
        _logger.LogTrace(nameof(CreateRestoreFolderReconciliationAsync));
        var pathComparer = restorePlan.Namespace.PathComparer;
        var desiredFiles = restorePlan.Files.ToDictionary(
            file => file.RelativePath,
            pathComparer);
        var filesToWrite = restorePlan.Files
            .Where(file => !blockedRestorePaths.Contains(file.RelativePath))
            .ToHashSet();
        var historyMoves = new RestoreHistoryPlan(pathComparer);
        try
        {
            var changedDesiredPaths = new HashSet<string>(pathComparer);
            var unchangedDesiredPaths = new HashSet<string>(pathComparer);
            var failedDesiredPaths = blockedRestorePaths.ToHashSet(pathComparer);
            var failures = new List<SyncItemFailure>();
            var extraCount = 0;

            var movedFolderPaths = new HashSet<string>(pathComparer);
            var orderedDestinationDirectories = destinationState.Directories
                .OrderBy(ArchivePathRules.GetPathDepth)
                .ThenBy(path => path, StringComparer.Ordinal);
            foreach (var directoryPath in orderedDestinationDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsUnderRestoreFolderMove(directoryPath, movedFolderPaths))
                {
                    continue;
                }

                if (desiredFiles.ContainsKey(directoryPath))
                {
                    historyMoves.Add(new(directoryPath, IsFolder: true));
                    movedFolderPaths.Add(directoryPath);
                    changedDesiredPaths.Add(directoryPath);
                    continue;
                }

                if (!restorePlan.IsDirectory(directoryPath) &&
                    (destinationLayout is null ||
                        !RestoreHistoryProtection.IsProtectedDirectory(directoryPath, destinationLayout)))
                {
                    historyMoves.Add(new(directoryPath, IsFolder: true));
                    movedFolderPaths.Add(directoryPath);
                    extraCount++;
                }
            }

            var remainingDestinationFiles = destinationState.Files.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                pathComparer);
            var changedDestinationFilePaths = new HashSet<string>(pathComparer);

            Task<RestoreDestinationFile?> TakeExistingRestoreObjectAsync
            (
                ArchiveProjectedObject desiredObject,
                string relativePath,
                CancellationToken currentCancellationToken
            )
            {
                _ = desiredObject;
                currentCancellationToken.ThrowIfCancellationRequested();
                if (IsUnderRestoreFolderMove(relativePath, movedFolderPaths))
                {
                    return Task.FromResult<RestoreDestinationFile?>(null);
                }

                remainingDestinationFiles.Remove(relativePath, out var destinationFile);
                return Task.FromResult(destinationFile);
            }

            async Task ConsumeRestoreObjectAsync
            (
                ArchiveProjectedObject desiredObject,
                string relativePath,
                RestoreDestinationFile? destinationFile,
                CancellationToken currentCancellationToken
            )
            {
                _ = desiredObject;
                if (destinationFile is null) { return; }

                var desiredFile = desiredFiles[relativePath];
                if (failedDesiredPaths.Contains(relativePath)) { return; }

                if (!byteForByte &&
                    logicalStateEntries.TryGetValue(relativePath, out var logicalStateEntry) &&
                    ArchiveChangeFingerprint.TryCreate(
                        destinationFile.ContentLength,
                        destinationFile.LastModifiedUtc,
                        out var currentStatFingerprint) &&
                    string.Equals(
                        currentStatFingerprint,
                        logicalStateEntry.StatFingerprint,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        desiredFile.ContentHash,
                        logicalStateEntry.ContentHash,
                        StringComparison.Ordinal))
                {
                    filesToWrite.Remove(desiredFile);
                    unchangedDesiredPaths.Add(relativePath);
                    return;
                }

                _logger.ReportObjectReadOperation(relativePath, "restore destination comparison");
                string destinationHash;
                try
                {
                    destinationHash = await ArchiveContentTransfer.ComputeStoredObjectHashAsync(
                        destinationStore,
                        destinationFile.ArchiveKey,
                        currentCancellationToken);
                }
                catch (Exception ex)
                {
                    currentCancellationToken.ThrowIfCancellationRequested();
                    if (ex is OperationCanceledException) { throw; }

                    filesToWrite.Remove(desiredFile);
                    failedDesiredPaths.Add(relativePath);
                    failures.Add(_logger.CreateReadFailure(
                        relativePath,
                        "restore destination comparison",
                        ex));
                    return;
                }
                if (string.Equals(
                        destinationHash,
                        desiredFile.ContentHash,
                        StringComparison.Ordinal))
                {
                    filesToWrite.Remove(desiredFile);
                    unchangedDesiredPaths.Add(relativePath);
                    return;
                }

                changedDestinationFilePaths.Add(relativePath);
                changedDesiredPaths.Add(relativePath);
            }

            await ArchiveReconciler.ReconcileAsync
            (
                restorePlan.Files,
                restorePlan.Directories,
                pathComparer,
                static _ => { },
                TakeExistingRestoreObjectAsync,
                ConsumeRestoreObjectAsync,
                cancellationToken
            );

            var orderedDestinationFiles = destinationState.Files.Values
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal);
            foreach (var destinationFile in orderedDestinationFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsUnderRestoreFolderMove(destinationFile.RelativePath, movedFolderPaths))
                {
                    continue;
                }

                if (changedDestinationFilePaths.Contains(destinationFile.RelativePath))
                {
                    historyMoves.Add(new(destinationFile.RelativePath, IsFolder: false));
                    continue;
                }

                if (!remainingDestinationFiles.ContainsKey(destinationFile.RelativePath))
                {
                    continue;
                }

                historyMoves.Add(new(destinationFile.RelativePath, IsFolder: false));
                if (restorePlan.IsDirectory(destinationFile.RelativePath))
                {
                    changedDesiredPaths.Add(destinationFile.RelativePath);
                }
                else
                {
                    extraCount++;
                }
            }

            foreach (var emptyDirectory in restorePlan.EmptyDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (changedDesiredPaths.Contains(emptyDirectory)) { continue; }

                if (destinationState.Directories.Contains(emptyDirectory))
                {
                    unchangedDesiredPaths.Add(emptyDirectory);
                }
            }

            var newDesiredPaths = desiredFiles.Keys
                .Concat(restorePlan.EmptyDirectories)
                .Where(path =>
                    !changedDesiredPaths.Contains(path) &&
                    !unchangedDesiredPaths.Contains(path) &&
                    !failedDesiredPaths.Contains(path))
                .ToHashSet(pathComparer);

            return new
            (
                filesToWrite
                    .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                    .ToArray(),
                historyMoves,
                newDesiredPaths,
                changedDesiredPaths,
                unchangedDesiredPaths,
                NewCount: newDesiredPaths.Count,
                ChangedCount: changedDesiredPaths.Count,
                ExtraCount: extraCount,
                UnchangedCount: unchangedDesiredPaths.Count,
                Failures: failures
            );
        }
        catch (Exception)
        {
            historyMoves.Dispose();
            throw;
        }
    }

    private static bool IsUnderRestoreFolderMove
    (
        string relativePath,
        HashSet<string> movedFolderPaths
    )
    {
        var candidatePath = relativePath;
        while (!string.IsNullOrEmpty(candidatePath))
        {
            if (movedFolderPaths.Contains(candidatePath)) { return true; }

            candidatePath = ArchivePathRules.GetParentPrefix(candidatePath);
        }

        return false;
    }

    internal async Task<RestoreDestinationState> LoadRestoreDestinationStateAsync
    (
        IObjectStore destinationStore,
        ArchiveLayout destinationLayout,
        StringComparer pathComparer,
        CancellationToken cancellationToken,
        string relativeRootPath = ""
    )
    {
        _logger.LogTrace(nameof(LoadRestoreDestinationStateAsync));

        var files = new Dictionary<string, RestoreDestinationFile>(pathComparer);
        var directories = new HashSet<string>(pathComparer);

        async Task LoadFolderAsync(string relativeFolderPath)
        {
            var folderKey = destinationLayout.ToLiveObjectKey(relativeFolderPath);
            _logger.LogObjectRead(
                string.IsNullOrEmpty(relativeFolderPath) ? "." : relativeFolderPath,
                "restore destination enumeration");
            var folderItems = destinationStore.GetFolderItemsAsync(
                folderKey,
                recursive: false,
                cancellationToken);
            await foreach (var item in folderItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var archiveKey = ArchiveLayout.NormalizeObjectKey(item.Key);
                if (ArchivePathRules.IsInternalObjectPrefix(archiveKey,
                        ArchivePathRules.CreateInternalObjectPrefixes(destinationLayout)) ||
                    (item.IsObject && ArchivePathRules.IsInternalObjectKey(archiveKey,
                        ArchivePathRules.CreateInternalObjectKeys(destinationLayout)))) { continue; }

                var relativePath = ArchiveLayout.RemovePrefix(
                    archiveKey,
                    destinationLayout.LivePrefix);
                if (item.IsFolder)
                {
                    if (!directories.Add(relativePath))
                    {
                        throw new YabtSyncException(
                            $"Restore destination folder '{relativePath}' was listed more than once.");
                    }

                    await LoadFolderAsync(relativePath);
                    continue;
                }

                if (item.Object is not null &&
                    !files.TryAdd(
                        relativePath,
                        new(
                            relativePath,
                            archiveKey,
                            item.Object.ContentLength,
                            item.Object.LastModifiedUtc)))
                {
                    throw new YabtSyncException(
                        $"Restore destination file '{relativePath}' was listed more than once.");
                }
            }
        }

        if (relativeRootPath.Length != 0) { directories.Add(relativeRootPath); }
        await LoadFolderAsync(relativeRootPath);
        return new(files, directories);
    }
}

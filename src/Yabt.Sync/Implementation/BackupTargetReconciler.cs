using Microsoft.Extensions.Logging;
using Yabt.Common;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class BackupTargetReconciler
(
    ILogger<BackupTargetReconciler> _logger
)
{
    internal async Task<List<BackupRepresentationConflict>> PlanBackupRepresentationConflictsAsync
    (
        IObjectStore targetStore,
        ArchiveLayout targetLayout,
        IObjectStoreNamespace targetNamespace,
        IEnumerable<ArchiveProjectedObject> desiredObjects,
        Dictionary<string, TargetFolderState> targetFolders,
        HashSet<string> absentTargetFolders,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(PlanBackupRepresentationConflictsAsync));

        var desiredFiles = desiredObjects.Select(item => item.RelativePath).
            ToLookup(ArchivePathRules.GetParentPrefix, StringComparer.Ordinal);
        var desiredFolders = new HashSet<string>(StringComparer.Ordinal) { string.Empty };
        foreach (var group in desiredFiles)
        {
            var folder = ObjectPath.Parse(group.Key);
            while (desiredFolders.Add(folder.Value))
            {
                folder = folder.Parent;
            }
        }

        var desiredChildFolders = desiredFolders.Where(path => path.Length != 0).
            ToLookup(ArchivePathRules.GetParentPrefix, StringComparer.Ordinal);
        var orderedFolders = desiredFolders.OrderBy(ArchivePathRules.GetPathDepth).
            ThenBy(path => path, StringComparer.Ordinal);
        var conflicts = new List<BackupRepresentationConflict>();
        foreach (var folderPath in orderedFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetFolder = await LoadTargetFolderStateAsync
            (
                targetStore,
                targetLayout,
                targetFolders,
                folderPath,
                cancellationToken
            );

            // These indexes exist only during read-only planning. Provider comparison
            // semantics can change when an existing directory is replaced.
            var objectPaths = targetFolder.Objects.Keys.ToDictionary
            (
                path => path,
                targetNamespace.PathComparer
            );
            var folderPaths = targetFolder.Folders.Keys.ToDictionary
            (
                path => path,
                targetNamespace.PathComparer
            );
            foreach (var desiredFolder in desiredChildFolders[folderPath])
            {
                if (objectPaths.TryGetValue(desiredFolder, out var blockingObject))
                {
                    conflicts.Add(new(blockingObject, IsFolder: false));
                    targetFolder.Objects.Remove(blockingObject);
                }

                if (folderPaths.TryGetValue(desiredFolder, out var existingFolder))
                {
                    if (string.Equals(existingFolder, desiredFolder, StringComparison.Ordinal)) { continue; }

                    conflicts.Add(new(existingFolder, IsFolder: true));
                    targetFolder.Folders.Remove(existingFolder);
                }

                // A missing or historized subtree is empty for both execution and
                // dry runs. Never probe an obsolete alias or a file as a directory.
                targetFolders[desiredFolder] = new();
                absentTargetFolders.Add(desiredFolder);
            }

            foreach (var desiredFile in desiredFiles[folderPath])
            {
                if (folderPaths.TryGetValue(desiredFile, out var blockingFolder))
                {
                    conflicts.Add(new(blockingFolder, IsFolder: true));
                    targetFolder.Folders.Remove(blockingFolder);
                }

                if (objectPaths.TryGetValue(desiredFile, out var existingFile) &&
                    !string.Equals(existingFile, desiredFile, StringComparison.Ordinal))
                {
                    conflicts.Add(new(existingFile, IsFolder: false));
                    targetFolder.Objects.Remove(existingFile);
                }
            }
        }

        return conflicts;
    }

    internal async Task ApplyBackupRepresentationConflictsAsync
    (
        IObjectStore targetStore,
        ArchiveHistorizer? historizer,
        ArchiveLayout targetLayout,
        IEnumerable<BackupRepresentationConflict> conflicts,
        DiskDictionary<ArchiveChangeManifestEntry> previousManifestEntries,
        ArchiveSyncSummary summary,
        bool writeChanges,
        bool verifyOnly,
        Func<CancellationToken, Task> beforeWriteAsync,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(ApplyBackupRepresentationConflictsAsync));

        const string reason = "blocks the desired representation";
        foreach (var conflict in conflicts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (conflict.IsFolder)
            {
                await MoveTargetFolderToHistoryAsync
                (
                    targetStore,
                    historizer,
                    targetLayout,
                    conflict.RelativePath,
                    previousManifestEntries,
                    summary,
                    writeChanges,
                    verifyOnly,
                    beforeWriteAsync,
                    cancellationToken,
                    reason
                );
                continue;
            }

            summary.AddExtra();
            previousManifestEntries.TryGetValue(conflict.RelativePath, out var previousManifestEntry);
            if (writeChanges)
            {
                await beforeWriteAsync(cancellationToken);
                await (historizer ??
                    throw new YabtSyncException("A mutating backup requires destination historization."))
                    .MoveObjectAsync(conflict.RelativePath, "Backup", cancellationToken);
                _logger.ReportBackupObjectHistorized(conflict.RelativePath, reason, projection: previousManifestEntry?.Projection);
            }
            else if (verifyOnly)
            {
                _logger.ReportVerifyObjectDifference(conflict.RelativePath, reason, projection: previousManifestEntry?.Projection);
            }
            else
            {
                _logger.ReportBackupWouldHistorizeObject(conflict.RelativePath, reason, projection: previousManifestEntry?.Projection);
            }
        }
    }

    internal async Task<TargetFolderState> LoadTargetFolderStateAsync
    (
        IObjectStore targetStore,
        ArchiveLayout targetLayout,
        Dictionary<string, TargetFolderState> targetFolders,
        string relativeFolderPrefix,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(LoadTargetFolderStateAsync));

        var normalizedRelativeFolderPrefix = ArchiveLayout.NormalizeObjectKey(relativeFolderPrefix);
        if (targetFolders.TryGetValue(normalizedRelativeFolderPrefix, out var cachedState))
        {
            return cachedState;
        }

        MarkTargetFolderVisited(
            targetFolders,
            normalizedRelativeFolderPrefix);

        var targetFolder = new TargetFolderState();
        var livePrefix = ArchiveLayout.NormalizeObjectPrefix(targetLayout.LivePrefix);
        var targetFolderPrefix = targetLayout.ToLiveObjectKey(normalizedRelativeFolderPrefix);
        _logger.LogObjectRead(
            string.IsNullOrEmpty(normalizedRelativeFolderPrefix) ?
                "." :
                normalizedRelativeFolderPrefix,
            "backup archive target enumeration");
        var folderItems = targetStore.GetFolderItemsAsync(
            targetFolderPrefix,
            recursive: false,
            cancellationToken);

        await foreach (var folderItem in folderItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetKey = ArchiveLayout.NormalizeObjectKey(folderItem.Key);
            if (ArchivePathRules.IsInternalObject(targetKey, targetLayout))
            {
                continue;
            }

            var relativePath = ArchiveLayout.RemovePrefix(targetKey, livePrefix);
            if (string.IsNullOrEmpty(relativePath))
            {
                continue;
            }

            if (folderItem.IsFolder)
            {
                targetFolder.Folders.TryAdd(relativePath, relativePath);
                continue;
            }

            if (folderItem.Object is not null)
            {
                targetFolder.Objects.TryAdd(relativePath, folderItem.Object with
                {
                    Key = targetKey,
                });
            }
        }

        var emptyFolderMarkerPath = ArchiveLayout.CombinePrefixAndRelativePath(
            normalizedRelativeFolderPrefix,
            ArchiveFolderMarkerFileNames.EmptyFolder);
        var emptyFolderMarkerKey = targetLayout.ToLiveObjectKey(emptyFolderMarkerPath);
        if (await targetStore.ExistsAsync(emptyFolderMarkerKey, cancellationToken))
        {
            targetFolder.Objects.TryAdd(
                emptyFolderMarkerPath,
                new(emptyFolderMarkerKey));
        }

        targetFolders[normalizedRelativeFolderPrefix] = targetFolder;
        return targetFolder;
    }

    private static void MarkTargetFolderVisited
    (
        Dictionary<string, TargetFolderState> targetFolders,
        string relativeFolderPrefix
    )
    {
        var parentPrefix = ArchivePathRules.GetParentPrefix(relativeFolderPrefix);
        if (targetFolders.TryGetValue(parentPrefix, out var parentFolder))
        {
            parentFolder.Folders.Remove(relativeFolderPrefix);
        }
    }

    internal async Task ReconcileDesiredTargetStateAsync
    (
        IObjectStore targetStore,
        ArchiveLayout targetLayout,
        Dictionary<string, TargetFolderState> targetFolders,
        IReadOnlySet<string> desiredFolderPaths,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(ReconcileDesiredTargetStateAsync));

        var orderedDesiredFolderPaths = desiredFolderPaths
            .OrderBy(ArchivePathRules.GetPathDepth)
            .ThenBy(path => path, StringComparer.Ordinal);
        foreach (var desiredFolderPath in orderedDesiredFolderPaths)
        {
            await LoadTargetFolderStateAsync(
                targetStore,
                targetLayout,
                targetFolders,
                desiredFolderPath,
                cancellationToken);
        }

        foreach (var desiredFolderPath in desiredFolderPaths)
        {
            var parentPrefix = ArchivePathRules.GetParentPrefix(desiredFolderPath);
            if (targetFolders.TryGetValue(parentPrefix, out var parentFolder))
            {
                parentFolder.Folders.Remove(desiredFolderPath);
            }
        }
    }

    internal async Task MoveRemainingTargetObjectsToHistoryAsync
    (
        IObjectStore targetStore,
        ArchiveHistorizer? historizer,
        ArchiveLayout targetLayout,
        Dictionary<string, TargetFolderState> targetFolders,
        IReadOnlySet<string> desiredFolderPaths,
        DiskDictionary<ArchiveChangeManifestEntry> previousManifestEntries,
        ArchiveSyncSummary summary,
        bool writeChanges,
        bool verifyOnly,
        Func<CancellationToken, Task> beforeFirstWriteAsync,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(MoveRemainingTargetObjectsToHistoryAsync));

        var extraFolderPaths = targetFolders.Values
            .SelectMany(targetFolder => targetFolder.Folders.Keys)
            .Distinct(StringComparer.Ordinal);
        var topLevelExtraFolderPaths = ArchivePathRules.SelectTopLevelFolderPaths(extraFolderPaths);

        foreach (var targetFolder in targetFolders.Values)
        {
            foreach (var relativePath in targetFolder.Objects.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (topLevelExtraFolderPaths.Any(
                        folderPath => !string.Equals(relativePath, folderPath, StringComparison.Ordinal) &&
                            ArchiveLayout.IsUnderPrefix(relativePath, folderPath)))
                {
                    continue;
                }

                summary.AddExtra();
                var historizationReason = ArchivePathRules.IsEmptyFolderMarker(relativePath) &&
                    desiredFolderPaths.Contains(ArchivePathRules.GetParentPrefix(relativePath)) ?
                        "empty directory changed" :
                        "removed from the source";
                previousManifestEntries.TryGetValue(relativePath, out var previousManifestEntry);
                if (writeChanges)
                {
                    await beforeFirstWriteAsync(cancellationToken);

                    await (historizer ??
                        throw new YabtSyncException(
                            "A mutating backup requires destination historization."))
                        .MoveObjectAsync(
                        relativePath,
                        "Backup",
                        cancellationToken);
                    _logger.ReportBackupObjectHistorized(
                        relativePath,
                        historizationReason,
                        projection: previousManifestEntry?.Projection);
                }
                else if (verifyOnly)
                {
                    _logger.ReportVerifyObjectDifference(
                        relativePath,
                        string.Equals(
                            historizationReason,
                            "empty directory changed",
                            StringComparison.Ordinal) ?
                                "the directory is no longer empty" :
                                "extra in the archive",
                        projection: previousManifestEntry?.Projection);
                }
                else
                {
                    _logger.ReportBackupWouldHistorizeObject(
                        relativePath,
                        historizationReason,
                        projection: previousManifestEntry?.Projection);
                }
            }
        }

        foreach (var relativeFolderPath in topLevelExtraFolderPaths)
        {
            await MoveTargetFolderToHistoryAsync(
                targetStore,
                historizer,
                targetLayout,
                relativeFolderPath,
                previousManifestEntries,
                summary,
                writeChanges,
                verifyOnly,
                beforeFirstWriteAsync,
                cancellationToken);
        }

    }

    private async Task MoveTargetFolderToHistoryAsync
    (
        IObjectStore targetStore,
        ArchiveHistorizer? historizer,
        ArchiveLayout targetLayout,
        string relativeFolderPath,
        DiskDictionary<ArchiveChangeManifestEntry> previousManifestEntries,
        ArchiveSyncSummary summary,
        bool writeChanges,
        bool verifyOnly,
        Func<CancellationToken, Task> beforeWriteAsync,
        CancellationToken cancellationToken,
        string reason = "removed from the source"
    )
    {
        _logger.LogTrace(nameof(MoveTargetFolderToHistoryAsync));

        var targetFolderPrefix = targetLayout.ToLiveObjectKey(relativeFolderPath);
        var targetItems = targetStore.GetFolderItemsAsync(
            targetFolderPrefix,
            recursive: true,
            cancellationToken);
        var livePrefix = ArchiveLayout.NormalizeObjectPrefix(targetLayout.LivePrefix);
        var visibleObjectPaths = new List<string>();

        await foreach (var targetItem in targetItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (targetItem.Object is null)
            {
                continue;
            }

            var targetObject = targetItem.Object;
            var targetKey = ArchiveLayout.NormalizeObjectKey(targetObject.Key);
            if (ArchivePathRules.IsInternalObject(targetKey, targetLayout))
            {
                continue;
            }

            var relativePath = ArchiveLayout.RemovePrefix(targetKey, livePrefix);
            if (string.IsNullOrEmpty(relativePath))
            {
                continue;
            }

            visibleObjectPaths.Add(relativePath);
            summary.AddExtra();
        }

        if (visibleObjectPaths.Count == 0)
        {
            summary.AddExtra();
        }

        if (!writeChanges)
        {
            if (visibleObjectPaths.Count == 0)
            {
                if (verifyOnly)
                {
                    _logger.ReportVerifyObjectDifference(
                        relativeFolderPath,
                        "extra in the archive",
                        isDirectory: true);
                }
                else
                {
                    _logger.ReportBackupWouldHistorizeObject(
                        relativeFolderPath,
                        reason,
                        isDirectory: true);
                }

                return;
            }

            foreach (var relativePath in visibleObjectPaths)
            {
                previousManifestEntries.TryGetValue(relativePath, out var previousManifestEntry);
                if (verifyOnly)
                {
                    _logger.ReportVerifyObjectDifference(
                        relativePath,
                        "extra in the archive",
                        projection: previousManifestEntry?.Projection);
                }
                else
                {
                    _logger.ReportBackupWouldHistorizeObject(
                        relativePath,
                        reason,
                        projection: previousManifestEntry?.Projection);
                }
            }
            return;
        }

        await beforeWriteAsync(cancellationToken);

        await (historizer ??
            throw new YabtSyncException(
                "A mutating backup requires destination historization."))
            .MoveFolderAsync(
            relativeFolderPath,
            "Backup",
            cancellationToken);

        if (visibleObjectPaths.Count == 0)
        {
            _logger.ReportBackupObjectHistorized(
                relativeFolderPath,
                reason,
                isDirectory: true);
            return;
        }

        foreach (var relativePath in visibleObjectPaths)
        {
            previousManifestEntries.TryGetValue(relativePath, out var previousManifestEntry);
            _logger.ReportBackupObjectHistorized(
                relativePath,
                reason,
                projection: previousManifestEntry?.Projection);
        }
    }
}

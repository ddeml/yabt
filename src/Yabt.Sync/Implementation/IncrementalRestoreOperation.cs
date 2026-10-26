using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yabt.Common;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class IncrementalRestoreOperation
(
    ILogger<IncrementalRestoreOperation> _logger,
    IOptionsMonitor<YabtSyncOptions> _options,
    ArchiveRootDocuments _rootDocuments,
    RestorePlanner _planner,
    RestoreReconciler _reconciler,
    RestoreExecutor _executor,
    ArchiveManifestStore _manifests,
    OperationProgressFactory _progressFactory
)
{
    internal async Task<SyncRunResult> RunAsync
    (
        SyncRunRequest request,
        RestoreContext context,
        ArchiveChangeManifest manifest,
        IArchiveRestoreFolderProjector projector,
        IRestoreDestination target,
        ArchiveLayout layout,
        RootDescriptorRestore descriptorRestore,
        bool rootNeedsCreation,
        int maximumConcurrency,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(RunAsync));
        if (target.Store is not IArchiveMutableObjectStore mutableStore)
        {
            throw new YabtSyncException("Restore destination must support guarded archive mutations.");
        }
        using var folders = new RestoreFolderIndex(manifest, cancellationToken);
        var source = new RestoreFolderSource(context.ArchiveStore, context.Descriptor.Layout, folders);
        var rootUnit = await source.ReadAsync(string.Empty, cancellationToken);
        await using (var rootPlan = await _planner.CreateRestoreFolderPlanAsync(projector,
            rootUnit.Objects, string.Empty, rootUnit.DesiredChildren,
            target.Namespace.Scope(ObjectPath.Parse(layout.LivePrefix)), cancellationToken))
        {
            RestorePlanner.ValidateRestorePlanInternalPaths(rootPlan, layout, target);
        }
        await using var mutationLock = request.DryRun ? null :
            await mutableStore.AcquireArchiveMutationLockAsync(cancellationToken,
                _options.CurrentValue.GetValidatedLockDocumentMaximumBytes());
        using var operationCancellation = mutationLock is null ? null :
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, mutationLock.LockLostToken);
        cancellationToken = operationCancellation?.Token ?? cancellationToken;
        if (mutationLock is not null)
        {
            var currentLayout = layout;
            if (context.ArchiveDocument is not null)
            {
                descriptorRestore = await _rootDocuments.InspectDestinationRootDescriptorAsync(
                    target, context.ArchiveDocument, request.ReplaceRootDescriptor, cancellationToken);
                currentLayout = context.ArchiveDocument.Descriptor.Layout;
            }
            else
            {
                currentLayout = await _rootDocuments.GetRestoreDestinationLayoutAsync(
                    target, context.Descriptor.Layout.HistPrefix, cancellationToken);
            }
            if (!ArchivePathRules.HaveSameLayout(layout, currentLayout))
            {
                throw new YabtSyncException("The destination layout changed while restore was preparing. Run restore again.");
            }
            await target.EnsureSafeAsync(layout.LivePrefix, layout.HistPrefix, cancellationToken);
        }

        using var logicalLoad = await _manifests.ReadLogicalStateManifestAsync(target.Store, cancellationToken);
        using var index = TemporaryIndex.Create();
        var destinationNamespace = target.Namespace.Scope(ObjectPath.Parse(layout.LivePrefix));
        using var logicalEntries = index.CreateDictionary<ArchiveLogicalStateManifestEntry>(
            comparer: destinationNamespace.PathComparer);
        if (logicalLoad.Manifest is not null)
        {
            foreach (var entry in logicalLoad.Manifest.Entries)
            {
                if (!logicalEntries.TryAdd(entry.LogicalRelativePath, entry))
                {
                    throw new YabtSyncException($"Logical-state manifest paths cannot be represented distinctly: '{entry.LogicalRelativePath}'.");
                }
            }
        }
        var session = _executor.CreateSession(mutableStore, layout, target, logicalLoad,
            descriptorRestore, context.ArchiveDocument);
        using var progress = request.DryRun ? null : _progressFactory.Open("restore",
            context.ArchiveStore, target.Store,
            [context.Descriptor.ArchiveId, layout.LivePrefix, layout.HistPrefix,
                manifest.ManifestHash, request.ReplaceRootDescriptor.ToString()]);
        using var pending = index.CreateCollection<string>();
        using var completed = index.CreateCollection<string>();
        using var seen = index.CreateSet();
        using var removed = index.CreateSet(destinationNamespace.PathComparer);
        using var createdDirectories = index.CreateSet(destinationNamespace.PathComparer);
        using var countedDirectoryChanges = index.CreateSet(destinationNamespace.PathComparer);
        using var finalFiles = new RestoreArchiveObjects(context.ArchiveStore, context.Descriptor.Layout);
        using var finalDirectories = index.CreateSet(destinationNamespace.PathComparer);
        using var nonemptyDirectories = index.CreateSet(destinationNamespace.PathComparer);
        using var deferredMoves = new RestoreHistoryPlan(destinationNamespace.PathComparer);
        var failures = new List<SyncItemFailure>();
        var newCount = 0;
        var changedCount = 0;
        var extraCount = 0;
        var unchangedCount = 0;
        var historyCount = 0;
        pending.Add(string.Empty);
        seen.Add(string.Empty);
        long pendingCursor = 0;
        long completedCursor = 0;
        while (pendingCursor < pending.LongCount || completedCursor < completed.LongCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = pendingCursor < pending.LongCount ? pending.Get(pendingCursor++) : completed.Get(completedCursor++);
            progress?.StartUnit(folder);
            var unit = folder.Length == 0 ? rootUnit : await source.ReadAsync(folder, cancellationToken);
            if (folder.Length == 0) { rootUnit = ([], [], []); }
            if (folders.ContainsFolder(folder))
            {
                var unitNamespace = destinationNamespace;
                if (request.DryRun)
                {
                    var removedAncestors = new List<ObjectPath>();
                    for (var path = ObjectPath.Parse(folder); !path.IsRoot; path = path.Parent)
                    {
                        if (removed.Contains(path.Value)) { removedAncestors.Add(path); }
                    }
                    unitNamespace = unitNamespace.AfterRemoving(removedAncestors);
                }
                await using var plan = await _planner.CreateRestoreFolderPlanAsync(projector,
                    unit.Objects, folder, unit.DesiredChildren, unitNamespace, cancellationToken);
                RestorePlanner.ValidateRestorePlanInternalPaths(plan, layout, target);
                var blocked = new HashSet<string>(unitNamespace.PathComparer);
                if (request.DryRun && request.ByteForByte)
                {
                    var readFailures = await _executor.ValidateRestorePlanContentAsync(
                        plan, maximumConcurrency, cancellationToken);
                    failures.AddRange(readFailures);
                    blocked.UnionWith(readFailures.Select(failure => failure.RelativePath));
                }
                var state = await LoadDestinationFolderAsync(target, layout, folder,
                    unitNamespace.PathComparer, cancellationToken);
                if (createdDirectories.Contains(folder))
                {
                    state = state with { Directories = state.Directories.Where(path => path != folder).ToHashSet(unitNamespace.PathComparer) };
                }
                using var originalReconciliation = await _reconciler.CreateRestoreFolderReconciliationAsync(
                    target.Store, plan, state, logicalEntries, request.ByteForByte, blocked, cancellationToken);
                var reconciliation = originalReconciliation;
                if (countedDirectoryChanges.Contains(folder) && plan.IsEmptyDirectory(folder))
                {
                    var newPaths = reconciliation.NewDesiredPaths.Where(path => path != folder).ToHashSet(unitNamespace.PathComparer);
                    var unchangedPaths = reconciliation.UnchangedDesiredPaths.Where(path => path != folder).ToHashSet(unitNamespace.PathComparer);
                    reconciliation = reconciliation with
                    {
                        NewDesiredPaths = newPaths,
                        UnchangedDesiredPaths = unchangedPaths,
                        NewCount = newPaths.Count,
                        UnchangedCount = unchangedPaths.Count,
                    };
                }
                newCount += reconciliation.NewCount;
                changedCount += reconciliation.ChangedCount;
                extraCount += reconciliation.ExtraCount;
                unchangedCount += reconciliation.UnchangedCount;
                historyCount += reconciliation.HistoryMoves.Count;
                failures.AddRange(reconciliation.Failures);
                if (request.DryRun)
                {
                    _logger.ReportRestoreDryRun(plan, reconciliation,
                        folder.Length == 0 ? descriptorRestore : RootDescriptorRestore.None,
                        folder.Length == 0 && rootNeedsCreation);
                    foreach (var move in reconciliation.HistoryMoves)
                    {
                        removed.Add(move.RelativePath);
                    }
                }
                else
                {
                    failures.AddRange(await _executor.ApplyRestoreFilesAsync(plan, reconciliation,
                        layout, target, request.ByteForByte, maximumConcurrency,
                        (file, token) => session.BeforeFileCommitAsync(reconciliation, file, token),
                        cancellationToken));
                    if (failures.Count != 0) { break; }
                    foreach (var directory in plan.Directories)
                    {
                        if (!target.DirectoryExists(ObjectPath.Parse(layout.ToLiveObjectKey(directory))))
                        {
                            createdDirectories.Add(directory);
                        }
                        if (reconciliation.ChangedDesiredPaths.Contains(directory))
                        {
                            countedDirectoryChanges.Add(directory);
                        }
                    }
                    await session.CreateUnitDirectoriesAsync(plan, reconciliation, cancellationToken);
                    foreach (var move in reconciliation.HistoryMoves.EnumeratePending(cancellationToken))
                    {
                        deferredMoves.Add(move);
                    }
                }
                foreach (var file in plan.Files)
                {
                    finalFiles.Add(new(file.RelativePath, file.ContentLength, file.LastModifiedUtc,
                        file.ContentHash, file.ChangeFingerprint));
                    for (var parent = ObjectPath.Parse(file.RelativePath).Parent;
                        !parent.IsRoot; parent = parent.Parent)
                    {
                        nonemptyDirectories.Add(parent.Value);
                    }
                }
                foreach (var directory in plan.Directories) { finalDirectories.Add(directory); }
            }
            progress?.CompleteUnit(folder, unit.Children);
            foreach (var child in unit.Children)
            {
                if (!seen.Add(child)) { continue; }
                var queue = progress?.HasUnfinishedSubtree(child) == false ? completed : pending;
                queue.Add(child);
            }
        }
        failures = SyncFailureReporting.NormalizeFailures(failures);
        var operationName = request.DryRun ? "Restore dry run" : "Restore";
        if (failures.Count != 0)
        {
            _logger.LogArchiveSyncIncomplete(operationName, failures.Count);
            return new(false, SyncFailureReporting.BuildIncompleteReadMessage(operationName, failures,
                request.DryRun ? "No changes were made." :
                    "Readable files may have been restored as rerun checkpoints; " +
                    "unrelated extra items and final manifests were left untouched."),
                newCount, changedCount, extraCount, unchangedCount, failures);
        }
        if (!request.DryRun)
        {
            var directoryIndex = RestoreDirectoryIndex.Create(finalDirectories, nonemptyDirectories,
                destinationNamespace.PathComparer, cancellationToken);
            await using var completePlan = new RestorePlan(finalFiles, directoryIndex, [],
                new Dictionary<string, IReadOnlyList<ArchiveRestoreProjection>>(StringComparer.Ordinal),
                destinationNamespace);
            var completeReconciliation = new RestoreReconciliation([], deferredMoves,
                new HashSet<string>(), new HashSet<string>(), new HashSet<string>(),
                newCount, changedCount, extraCount, unchangedCount, []);
            await session.CompleteAsync(completePlan, completeReconciliation, rootNeedsCreation, cancellationToken);
        }
        progress?.CompleteOperation();
        _logger.LogArchiveSyncCompleted(operationName, newCount, changedCount, extraCount, unchangedCount);
        return new(true, $"{operationName} completed; {newCount} new item(s), " +
            $"{changedCount} changed item(s), {extraCount} extra item(s), and " +
            $"{unchangedCount} unchanged item(s). {historyCount} existing item(s) " +
            $"{(request.DryRun ? "would be moved" : "were moved")} to history.",
            newCount, changedCount, extraCount, unchangedCount);
    }

    private async Task<RestoreDestinationState> LoadDestinationFolderAsync
    (
        IRestoreDestination target,
        ArchiveLayout layout,
        string folder,
        StringComparer comparer,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(LoadDestinationFolderAsync));
        var files = new Dictionary<string, RestoreDestinationFile>(comparer);
        var directories = new HashSet<string>(comparer);
        var folderKey = layout.ToLiveObjectKey(folder);
        if (!target.DirectoryExists(ObjectPath.Parse(folderKey))) { return new(files, directories); }
        if (folder.Length != 0) { directories.Add(folder); }
        var items = target.Store.GetFolderItemsAsync(folderKey, false, cancellationToken);
        await foreach (var item in items)
        {
            var key = ObjectPath.Parse(item.Key);
            if (key.IsRoot || key.Parent != ObjectPath.Parse(folderKey))
            {
                throw new YabtSyncException($"Restore destination folder '{folder}' listed non-child '{key}'.");
            }
            if (ArchivePathRules.IsInternalObject(key.Value, layout)) { continue; }
            var relativePath = ArchiveLayout.RemovePrefix(key.Value, layout.LivePrefix);
            if (item.IsFolder)
            {
                if (!directories.Add(relativePath))
                {
                    throw new YabtSyncException($"Restore destination has equivalent folder '{relativePath}'.");
                }
            }
            else if (item.Object is not null && !files.TryAdd(relativePath,
                new(relativePath, key.Value, item.Object.ContentLength, item.Object.LastModifiedUtc)))
            {
                throw new YabtSyncException($"Restore destination has equivalent file '{relativePath}'.");
            }
        }
        return new(files, directories);
    }
}

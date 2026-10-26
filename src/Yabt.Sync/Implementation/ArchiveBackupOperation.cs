using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yabt.Common;
using Yabt.Core;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class ArchiveBackupOperation
(
    ILogger<ArchiveBackupOperation> _logger,
    IArchiveLocationResolver _locationResolver,
    IChangeManifestSerializer _changeManifestSerializer,
    IOptionsMonitor<YabtSyncOptions> _options,
    IOptionsMonitor<TemporaryContentOptions> _temporaryOptions,
    TimeProvider _timeProvider,
    ArchiveOperationContextFactory _contexts,
    ArchiveRootDocuments _rootDocuments,
    BackupTargetReconciler _targetReconciler,
    ArchiveContentTransfer _contentTransfer,
    ArchiveManifestStore _manifests
)
{
    internal async Task<SyncRunResult> RunBackupOperationAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(RunBackupOperationAsync));

        _logger.LogBackupRequested(
            request.SourceRoot,
            request.DryRun);

        var context = await _contexts.CreateContextAsync(request, cancellationToken);
        using var temporaryStorage = TemporaryStorageValidation.BeginOperation(path =>
            _locationResolver.ValidateTemporaryStorageLocations(path, context.SourceLocation, context.TargetStore));
        TemporaryStorageValidation.ValidateDirectory(_temporaryOptions.CurrentValue.GetEffectiveTemporaryDirectory());
        using var temporaryIndexOperation = TemporaryIndex.BeginOperation(_temporaryOptions.CurrentValue);

        return await ApplyProjectionAsync(
            context,
            writeChanges: !request.DryRun,
            verifyOnly: false,
            byteForByte: request.ByteForByte,
            operationName: request.DryRun ? "backup dry run" : "backup",
            cancellationToken);
    }

    public async Task<SyncRunResult> VerifyAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(VerifyAsync));

        using var configurationWarnings = ConfigurationWarnings.BeginOperation();

        var context = await _contexts.CreateContextAsync(request, cancellationToken);
        using var temporaryStorage = TemporaryStorageValidation.BeginOperation(path =>
            _locationResolver.ValidateTemporaryStorageLocations(path, context.SourceLocation, context.TargetStore));
        TemporaryStorageValidation.ValidateDirectory(_temporaryOptions.CurrentValue.GetEffectiveTemporaryDirectory());
        using var temporaryIndexOperation = TemporaryIndex.BeginOperation(_temporaryOptions.CurrentValue);

        return await ApplyProjectionAsync(
            context,
            writeChanges: false,
            verifyOnly: true,
            byteForByte: request.ByteForByte,
            operationName: "verify",
            cancellationToken);
    }

    private async Task<SyncRunResult> ApplyProjectionAsync
    (
        ArchiveSyncContext context,
        bool writeChanges,
        bool verifyOnly,
        bool byteForByte,
        string operationName,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(ApplyProjectionAsync));

        await context.SourceStore.EnsureReadyAsync(cancellationToken);
        if (writeChanges)
        {
            await context.TargetStore.InitializeAsync(cancellationToken);
        }

        if (writeChanges && context.TargetStore is not IArchiveMutableObjectStore)
        {
            throw new YabtSyncException(
                "The selected target store does not provide the guarded mutations required " +
                "for synchronization.");
        }

        var mutableTargetStore = writeChanges ?
            (IArchiveMutableObjectStore)context.TargetStore :
            null;
        await using var mutationLock = mutableTargetStore is not null ?
            await mutableTargetStore.AcquireArchiveMutationLockAsync
            (
                cancellationToken,
                _options.CurrentValue.GetValidatedLockDocumentMaximumBytes()
            ) :
            null;
        using var operationCancellation = mutationLock is null ?
            null :
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                mutationLock.LockLostToken);
        if (operationCancellation is not null)
        {
            cancellationToken = operationCancellation.Token;
        }

        var changeManifestFileName = ArchiveManifestStore.GetChangeManifestFileName(
            context.TargetDescriptor.ChangeManifestCompression);
        using var changeManifestLoad = await _manifests.ReadChangeManifestAsync(
            context.TargetStore,
            recoverInvalidManifest: writeChanges || byteForByte,
            cancellationToken);
        var previousChangeManifest = changeManifestLoad.Manifest;
        using var manifestIndex = TemporaryIndex.Create();
        using var previousManifestEntries = manifestIndex.CreateDictionary<ArchiveChangeManifestEntry>();
        foreach (var entry in previousChangeManifest?.Entries ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            previousManifestEntries.Add(entry.RelativePath, entry);
        }
        using var nextManifestEntries = manifestIndex.CreateDictionary<ArchiveChangeManifestEntry>();

        var targetFolders = new Dictionary<string, TargetFolderState>(StringComparer.Ordinal);
        var absentTargetFolders = new HashSet<string>(StringComparer.Ordinal);
        await _targetReconciler.LoadTargetFolderStateAsync(
            context.TargetStore,
            context.TargetDescriptor.Layout,
            targetFolders,
            string.Empty,
            cancellationToken);

        var projectionRequest = _contexts.CreateProjectionRequest(context);
        await using var projectionSourceLifetime = (IAsyncDisposable)projectionRequest.SourceStore;
        await using var projectedObjects = context.FormatHandler.ProjectBackupAsync(
            projectionRequest,
            cancellationToken);
        var summary = new ArchiveSyncSummary();
        var sourceReadFailures = new List<SyncItemFailure>();
        var existingNonemptyDirectoryPaths = new HashSet<string>(StringComparer.Ordinal);
        RestoreProjectionKey? rootBackupProjectionKey = null;
        var rootBackupArtifactRoles = new HashSet<string>(StringComparer.Ordinal);
        var backupProjectionGroups = new Dictionary<
            RestoreProjectionKey,
            BackupProjectionConsistencyGroup>();

        void CaptureSourceReadFailures(Exception exception)
        {
            if (ObjectStoreOverlapException.IsContainedIn(exception))
            {
                throw new YabtSyncException
                (
                    $"Archive {operationName} stopped because source and target storage overlap.",
                    exception
                );
            }
            foreach (var sourceReadFailure in SourceObjectReadException.FindAll(exception))
            {
                sourceReadFailures.Add(_logger.CreateReadFailure(
                    sourceReadFailure.SourcePath,
                    "backup source read",
                    sourceReadFailure.InnerException ?? sourceReadFailure));
            }
        }
        var historizer = mutableTargetStore is null ?
            null :
            new ArchiveHistorizer(
                mutableTargetStore,
                context.TargetDescriptor.Layout,
                _timeProvider.GetUtcNow(),
                _logger);
        if (historizer is not null)
        {
            await historizer.InspectRecoveryStateAsync(cancellationToken);
        }

        var rootDescriptorExists = false;
        var rootDescriptorNeedsWrite = false;
        if (context.SourceDocument is not null)
        {
            rootDescriptorExists = await context.TargetStore.ExistsAsync(
                BackupRootFileNames.Primary,
                cancellationToken);
            if (rootDescriptorExists)
            {
                var targetRootDocument = await _rootDocuments.ReadStoredRootDocumentAsync(
                    context.TargetStore,
                    "Target",
                    cancellationToken);
                ArchiveRootDocuments.ValidateRootDescriptorManifestEvidence(
                    previousChangeManifest,
                    targetRootDocument);
                if (!string.Equals(
                        context.SourceDocument.Descriptor.ArchiveId,
                        targetRootDocument.Descriptor.ArchiveId,
                        StringComparison.Ordinal) ||
                    !ArchivePathRules.HaveSameLayout(
                        context.SourceDocument.Descriptor.Layout,
                        targetRootDocument.Descriptor.Layout))
                {
                    throw new YabtSyncException(
                        "The selected target contains a root descriptor for a different archive " +
                            "id or layout. YABT will not combine those archives.");
                }

                rootDescriptorNeedsWrite = !context.SourceDocument.ContentEquals(
                    targetRootDocument);
                if (!rootDescriptorNeedsWrite)
                {
                    _logger.LogControlMetadataOperation(
                        "Determined unchanged",
                        BackupRootFileNames.Primary);
                }
            }
            else
            {
                rootDescriptorNeedsWrite = true;
            }
        }

        if (rootDescriptorNeedsWrite && !writeChanges)
        {
            if (verifyOnly)
            {
                _logger.LogControlMetadataOperation(
                    "Verify found missing or different",
                    BackupRootFileNames.Primary);
            }
            else
            {
                if (rootDescriptorExists)
                {
                    _logger.LogControlMetadataOperation(
                        "Would move to history (replacement)",
                        BackupRootFileNames.Primary);
                }

                _logger.LogControlMetadataOperation(
                    rootDescriptorExists ?
                        "Would write replacement" :
                        "Would write missing",
                    BackupRootFileNames.Primary);
            }
        }

        var manifestTransaction = mutableTargetStore is null ?
            null :
            new BackupManifestTransaction
            (
                _manifests,
                mutableTargetStore,
                changeManifestLoad,
                changeManifestFileName,
                _logger
            );

        Task EnsureChangeManifestInvalidatedAsync(CancellationToken currentCancellationToken) =>
            (manifestTransaction ??
                throw new YabtSyncException("A mutating backup requires a manifest transaction."))
                .InvalidateAsync(currentCancellationToken);

        async Task<ArchiveObjectInfo?> TakeExistingBackupObjectAsync
        (
            ArchiveProjectedObject desiredObject,
            string relativePath,
            CancellationToken currentCancellationToken
        )
        {
            _ = desiredObject;
            var targetFolder = await _targetReconciler.LoadTargetFolderStateAsync(
                context.TargetStore,
                context.TargetDescriptor.Layout,
                targetFolders,
                ArchivePathRules.GetParentPrefix(relativePath),
                currentCancellationToken);

            if (ArchivePathRules.IsEmptyFolderMarker(relativePath) &&
                (targetFolder.Objects.Count != 0 || targetFolder.Folders.Count != 0))
            {
                existingNonemptyDirectoryPaths.Add(ArchivePathRules.GetParentPrefix(relativePath));
            }

            if (!targetFolder.Objects.Remove(relativePath, out var targetObject) &&
                ArchivePathRules.IsEmptyFolderMarker(relativePath) &&
                !absentTargetFolders.Contains(ArchivePathRules.GetParentPrefix(relativePath)) &&
                await context.TargetStore.ExistsAsync(
                    context.TargetDescriptor.Layout.ToLiveObjectKey(relativePath),
                    currentCancellationToken))
            {
                targetObject = new(context.TargetDescriptor.Layout.ToLiveObjectKey(relativePath));
            }

            return targetObject;
        }

        async Task ProcessBackupObjectAsync
        (
            ArchiveProjectedObject projectedObject,
            string relativePath,
            ArchiveObjectInfo? targetObject,
            CancellationToken currentCancellationToken
        )
        {
            BackupProjectionConsistencyGroup? projectionConsistencyGroup = null;
            if (projectedObject.Projection is not null)
            {
                var consistencyKey = new RestoreProjectionKey
                (
                    projectedObject.Projection.LogicalPath,
                    projectedObject.Projection.Format,
                    projectedObject.Projection.FormatVersion,
                    projectedObject.Projection.ProjectionId
                );
                if (!backupProjectionGroups.TryGetValue(
                        consistencyKey,
                        out projectionConsistencyGroup))
                {
                    projectionConsistencyGroup = new();
                    backupProjectionGroups.Add(
                        consistencyKey,
                        projectionConsistencyGroup);
                }
            }

            if (context.FormatHandler.ProjectsBesideSourceFolder)
            {
                var projection = projectedObject.Projection ??
                    throw new YabtSyncException(
                        $"Packaged root format handler '{context.FormatHandler.FormatName}' " +
                            "produced an artifact without projection provenance.");
                if (!string.IsNullOrEmpty(
                        ArchiveLayout.NormalizeObjectKey(projection.LogicalPath)) ||
                    !string.Equals(
                        projection.Format,
                        context.FormatHandler.FormatName,
                        StringComparison.Ordinal))
                {
                    throw new YabtSyncException(
                        $"Packaged root format handler '{context.FormatHandler.FormatName}' " +
                            "produced inconsistent root projection provenance.");
                }

                var projectionKey = new RestoreProjectionKey(
                    projection.LogicalPath,
                    projection.Format,
                    projection.FormatVersion,
                    projection.ProjectionId);
                rootBackupProjectionKey ??= projectionKey;
                if (rootBackupProjectionKey != projectionKey ||
                    !rootBackupArtifactRoles.Add(projection.ArtifactRole))
                {
                    throw new YabtSyncException(
                        $"Packaged root format handler '{context.FormatHandler.FormatName}' " +
                            "produced artifacts from multiple projections or with duplicate roles.");
                }
            }

            if (targetObject is not null)
            {
                previousManifestEntries.TryGetValue(relativePath, out var previousManifestEntry);
                var comparison = await _contentTransfer.CompareProjectedObjectAsync(
                        projectedObject,
                        context.TargetStore,
                        targetObject,
                        previousManifestEntry,
                        byteForByte,
                        prepareChangedContent: writeChanges,
                        currentCancellationToken);
                await using var preparedContent = comparison.PreparedContent;
                if (comparison.Same)
                {
                    summary.AddUnchanged();
                    projectionConsistencyGroup?.MatchedObjects.Add(new
                    (
                        projectedObject,
                        relativePath,
                        targetObject,
                        previousManifestEntry
                    ));
                    nextManifestEntries.Add(
                        relativePath,
                        comparison.ManifestEntry ??
                            throw new YabtSyncException(
                                $"Content comparison for '{relativePath}' did not produce manifest evidence."));
                    if (!writeChanges || byteForByte || projectionConsistencyGroup is null)
                    {
                        _logger.ReportBackupObjectUnchanged(
                            operationName,
                            relativePath,
                            projectedObject.Projection);
                    }
                    return;
                }

                summary.AddChanged();
                if (projectionConsistencyGroup is not null)
                {
                    projectionConsistencyGroup.RequiresValidation = true;
                }
                if (writeChanges)
                {
                    await EnsureChangeManifestInvalidatedAsync(currentCancellationToken);
                    await (historizer ??
                        throw new YabtSyncException(
                            "A mutating backup requires destination historization."))
                        .MoveObjectAsync(
                        relativePath,
                        "Backup",
                        currentCancellationToken);
                    _logger.ReportBackupObjectHistorized(
                        relativePath,
                        "replacement",
                        projection: previousManifestEntry?.Projection);

                    var manifestEntry = await _contentTransfer.UploadProjectedObjectAsync(
                        context.TargetStore,
                        context.TargetDescriptor.Layout,
                        projectedObject,
                        relativePath,
                        currentCancellationToken,
                        preparedContent);
                    nextManifestEntries.Add(relativePath, manifestEntry);
                    _logger.ReportBackupObjectChanged(relativePath, projectedObject.Projection);
                }
                else if (verifyOnly)
                {
                    _logger.ReportVerifyObjectDifference(
                        relativePath,
                        "content differs",
                        projection: projectedObject.Projection);
                }
                else
                {
                    _logger.ReportBackupWouldHistorizeObject(
                        relativePath,
                        "replacement",
                        projection: previousManifestEntry?.Projection);
                    _logger.ReportBackupWouldChangeObject(relativePath, projectedObject.Projection);
                }

                return;
            }

            await using var preparedNewContent = writeChanges ?
                await _contentTransfer.PrepareProjectedObjectContentAsync(
                    projectedObject,
                    relativePath,
                    "backup upload preparation",
                    currentCancellationToken) :
                null;

            summary.AddNew();
            if (projectionConsistencyGroup is not null)
            {
                projectionConsistencyGroup.RequiresValidation = true;
            }
            if (writeChanges)
            {
                await EnsureChangeManifestInvalidatedAsync(currentCancellationToken);

                var manifestEntry = await _contentTransfer.UploadProjectedObjectAsync(
                    context.TargetStore,
                    context.TargetDescriptor.Layout,
                    projectedObject,
                    relativePath,
                    currentCancellationToken,
                    preparedNewContent);
                nextManifestEntries.Add(relativePath, manifestEntry);
                _logger.ReportBackupObjectAdded(
                    relativePath,
                    existingNonemptyDirectoryPaths.Contains(ArchivePathRules.GetParentPrefix(relativePath)),
                    projectedObject.Projection);
            }
            else if (verifyOnly)
            {
                _logger.ReportVerifyObjectDifference(
                    relativePath,
                    "missing from the archive",
                    projection: projectedObject.Projection);
            }
            else
            {
                _logger.ReportBackupWouldAddObject(
                    relativePath,
                    existingNonemptyDirectoryPaths.Contains(ArchivePathRules.GetParentPrefix(relativePath)),
                    projectedObject.Projection);
            }
        }

        async Task CompleteBackupProjectionGroupAsync(BackupProjectionConsistencyGroup projectionGroup)
        {
            if (writeChanges && !byteForByte && !projectionGroup.SourceReadFailed)
            {
                if (!projectionGroup.RequiresValidation)
                {
                    foreach (var matchedObject in projectionGroup.MatchedObjects)
                    {
                        _logger.ReportBackupObjectUnchanged(
                            operationName,
                            matchedObject.RelativePath,
                            matchedObject.ProjectedObject.Projection);
                    }
                }
                else
                {
                    foreach (var matchedObject in projectionGroup.MatchedObjects)
                    {
                        try
                        {
                            var comparison = await _contentTransfer.CompareProjectedObjectAsync
                            (
                                matchedObject.ProjectedObject,
                                context.TargetStore,
                                matchedObject.TargetObject,
                                matchedObject.PreviousManifestEntry,
                                byteForByte: true,
                                prepareChangedContent: writeChanges,
                                cancellationToken
                            );
                            await using var preparedContent = comparison.PreparedContent;
                            if (comparison.Same)
                            {
                                if (writeChanges)
                                {
                                    nextManifestEntries[matchedObject.RelativePath] =
                                        comparison.ManifestEntry ??
                                        throw new YabtSyncException(
                                            "Projection consistency comparison for " +
                                                $"'{matchedObject.RelativePath}' did not produce " +
                                                "manifest evidence.");
                                }

                                _logger.ReportBackupObjectUnchanged(
                                    operationName,
                                    matchedObject.RelativePath,
                                    matchedObject.ProjectedObject.Projection);

                                continue;
                            }

                            summary.PromoteUnchangedToChanged();
                            if (!writeChanges) { continue; }

                            await EnsureChangeManifestInvalidatedAsync(cancellationToken);
                            await (historizer ??
                                throw new YabtSyncException(
                                    "A mutating backup requires destination historization."))
                                .MoveObjectAsync(
                                    matchedObject.RelativePath,
                                    "Backup",
                                    cancellationToken);
                            _logger.ReportBackupObjectHistorized(
                                matchedObject.RelativePath,
                                "projection consistency replacement",
                                projection: matchedObject.PreviousManifestEntry?.Projection);
                            var manifestEntry = await _contentTransfer.UploadProjectedObjectAsync
                            (
                                context.TargetStore,
                                context.TargetDescriptor.Layout,
                                matchedObject.ProjectedObject,
                                matchedObject.RelativePath,
                                cancellationToken,
                                preparedContent ??
                                    throw new YabtSyncException(
                                        "Changed projection artifact " +
                                            $"'{matchedObject.RelativePath}' did not retain its " +
                                            "validated source snapshot.")
                            );
                            nextManifestEntries[matchedObject.RelativePath] = manifestEntry;
                            _logger.ReportBackupObjectChanged(
                                matchedObject.RelativePath,
                                matchedObject.ProjectedObject.Projection);
                        }
                        catch (Exception ex) when (SourceObjectReadException.FindAll(ex).Count != 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            CaptureSourceReadFailures(ex);
                            projectionGroup.SourceReadFailed = true;
                            break;
                        }
                    }
                }
            }
            projectionGroup.MatchedObjects.Clear();
            if (projectionGroup.Projection is not null)
            {
                await ((ArchiveProjectionObjectStore)projectionRequest.SourceStore)
                    .ReleaseProjectionAsync(projectionGroup.Projection);
            }
            if (context.FormatHandler.ProjectsBesideSourceFolder)
            {
                await projectedObjects.DisposeAsync();
            }
        }

        async Task ConsumeBackupObjectAsync
        (
            ArchiveProjectedObject projectedObject,
            string relativePath,
            ArchiveObjectInfo? targetObject,
            CancellationToken currentCancellationToken
        )
        {
            try
            {
                await ProcessBackupObjectAsync(
                    projectedObject,
                    relativePath,
                    targetObject,
                    currentCancellationToken);
            }
            catch (Exception ex) when (SourceObjectReadException.FindAll(ex).Count != 0)
            {
                currentCancellationToken.ThrowIfCancellationRequested();
                if (ObjectStoreOverlapException.IsContainedIn(ex)) { throw; }
                CaptureSourceReadFailures(ex);
                if (projectedObject.Projection is not null)
                {
                    var projectionKey = new RestoreProjectionKey
                    (
                        projectedObject.Projection.LogicalPath,
                        projectedObject.Projection.Format,
                        projectedObject.Projection.FormatVersion,
                        projectedObject.Projection.ProjectionId
                    );
                    if (backupProjectionGroups.TryGetValue(
                            projectionKey,
                            out var projectionGroup))
                    {
                        projectionGroup.SourceReadFailed = true;
                    }
                }
            }
            if (projectedObject.Projection is not null)
            {
                var projection = projectedObject.Projection;
                var key = new RestoreProjectionKey(
                    projection.LogicalPath, projection.Format, projection.FormatVersion, projection.ProjectionId);
                if (backupProjectionGroups.TryGetValue(key, out var completedGroup) &&
                    --completedGroup.RemainingObjects == 0)
                {
                    await CompleteBackupProjectionGroupAsync(completedGroup);
                    backupProjectionGroups.Remove(key);
                }
            }
        }

        void RegisterProjectionGroups(IEnumerable<ArchiveProjectedObject> desiredObjects, bool useKnownCounts)
        {
            var groups = desiredObjects.Where(item => item.Projection is not null).GroupBy
            (
                item =>
                {
                    var projection = item.Projection!;
                    return new RestoreProjectionKey
                    (
                        projection.LogicalPath, projection.Format, projection.FormatVersion, projection.ProjectionId
                    );
                }
            );
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (backupProjectionGroups.ContainsKey(group.Key)) { continue; }

                var projection = group.First().Projection!;
                var count = group.Count();
                if (useKnownCounts &&
                    ((ArchiveProjectionObjectStore)projectionRequest.SourceStore).
                        TryGetProjectionArtifactCount(projection, out var completeCount))
                {
                    if (completeCount < count)
                    {
                        throw new YabtSyncException("A backup projection exceeded its complete artifact count.");
                    }
                    count = completeCount;
                }

                backupProjectionGroups.Add
                (
                    group.Key,
                    new()
                    {
                        Projection = projection,
                        RemainingObjects = count,
                    }
                );
            }
        }

        var folderProjector = context.FormatHandler.ProjectsBesideSourceFolder ? null :
            context.FormatHandler as IArchiveBackupFolderProjector;
        using var folderTraversal = folderProjector is null ? null : new BackupFolderTraversal(manifestIndex);
        IReadOnlySet<string> desiredFolderPaths;
        try
        {
            var targetNamespace = context.TargetStore.Namespace
                .Scope(ObjectPath.Parse(context.TargetDescriptor.Layout.LivePrefix));
            if (folderProjector is not null)
            {
                while (folderTraversal!.TryTake(out var work))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await using var unit = await folderProjector.ProjectBackupFolderAsync
                    (
                        projectionRequest, work.RelativePath, cancellationToken
                    );
                    BackupFolderTraversal.ValidateUnit(work, unit);
                    foreach (var item in unit.Objects)
                    {
                        ArchivePathRules.ValidateProjectedLiveObjectPath
                        (
                            item.RelativePath, context.TargetDescriptor.Layout
                        );
                    }
                    foreach (var child in unit.ChildFolderPaths)
                    {
                        ArchivePathRules.ValidateProjectedLiveFolderPath(child, context.TargetDescriptor.Layout);
                    }

                    // Dry runs retain the physical blockers. Recreated descendants
                    // must use their prospective ancestor semantics while planning.
                    var unitNamespace = writeChanges || work.RemovedAncestorPath is null ? targetNamespace :
                        targetNamespace.AfterRemoving([ObjectPath.Parse(work.RemovedAncestorPath)]);
                    var conflicts = await _targetReconciler.PlanBackupFolderRepresentationConflictsAsync
                    (
                        context.TargetStore,
                        context.TargetDescriptor.Layout,
                        unitNamespace,
                        unit.RelativePath,
                        unit.Objects,
                        unit.ChildFolderPaths,
                        targetFolders,
                        absentTargetFolders,
                        cancellationToken
                    );
                    unitNamespace.AfterRemoving(conflicts.Select(conflict => ObjectPath.Parse(conflict.RelativePath))).
                        ValidateTree
                        (
                            unit.Objects.Select(item => ObjectPath.Parse(item.RelativePath)),
                            unit.ChildFolderPaths.Select(ObjectPath.Parse)
                        );
                    RegisterProjectionGroups(unit.Objects, useKnownCounts: true);
                    await _targetReconciler.ApplyBackupRepresentationConflictsAsync
                    (
                        context.TargetStore, historizer, context.TargetDescriptor.Layout,
                        conflicts, previousManifestEntries, summary, writeChanges, verifyOnly,
                        EnsureChangeManifestInvalidatedAsync, cancellationToken
                    );
                    await ArchiveReconciler.ReconcileAsync
                    (
                        unit.Objects,
                        unit.ChildFolderPaths,
                        StringComparer.Ordinal,
                        static _ => { },
                        TakeExistingBackupObjectAsync,
                        ConsumeBackupObjectAsync,
                        cancellationToken
                    );
                    folderTraversal.ScheduleChildren(work, unit, conflicts, unitNamespace.PathComparer, cancellationToken);
                }

                desiredFolderPaths = folderTraversal.DesiredFolderPaths;
            }
            else
            {
                // Handlers without folder-local ownership retain complete projection
                // preflight, including a packaged selected root.
                var desiredObjects = new List<ArchiveProjectedObject>();
                await foreach (var projectedObject in projectedObjects.WithCancellation(cancellationToken))
                {
                    ArchivePathRules.ValidateProjectedLiveObjectPath
                    (
                        projectedObject.RelativePath, context.TargetDescriptor.Layout
                    );
                    desiredObjects.Add(projectedObject);
                }

                RegisterProjectionGroups(desiredObjects, useKnownCounts: false);
                var representationConflicts = await _targetReconciler.PlanBackupRepresentationConflictsAsync
                (
                    context.TargetStore, context.TargetDescriptor.Layout, targetNamespace,
                    desiredObjects, targetFolders, absentTargetFolders, cancellationToken
                );
                targetNamespace.AfterRemoving
                (
                    representationConflicts.Select(conflict => ObjectPath.Parse(conflict.RelativePath))
                ).ValidateTree(desiredObjects.Select(item => ObjectPath.Parse(item.RelativePath)), []);
                await _targetReconciler.ApplyBackupRepresentationConflictsAsync
                (
                    context.TargetStore, historizer, context.TargetDescriptor.Layout,
                    representationConflicts, previousManifestEntries, summary, writeChanges, verifyOnly,
                    EnsureChangeManifestInvalidatedAsync, cancellationToken
                );
                var reconciliationTraversal = await ArchiveReconciler.ReconcileAsync
                (
                    desiredObjects,
                    explicitDirectories: null,
                    StringComparer.Ordinal,
                    static _ => { },
                    TakeExistingBackupObjectAsync,
                    ConsumeBackupObjectAsync,
                    cancellationToken
                );
                desiredFolderPaths = reconciliationTraversal.DesiredFolderPaths;
            }
        }
        catch (Exception ex) when (SourceObjectReadException.FindAll(ex).Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureSourceReadFailures(ex);
            desiredFolderPaths = new HashSet<string>(StringComparer.Ordinal);
        }

        if (context.FormatHandler.ProjectsBesideSourceFolder &&
            rootBackupProjectionKey is null &&
            sourceReadFailures.Count == 0)
        {
            throw new YabtSyncException(
                $"Packaged root format handler '{context.FormatHandler.FormatName}' did not " +
                    "produce any projection artifacts.");
        }


        if (sourceReadFailures.Count == 0)
        {
            if (backupProjectionGroups.Count != 0)
            {
                throw new YabtSyncException("Backup traversal did not complete every projection artifact group.");
            }
            await _targetReconciler.ReconcileDesiredTargetStateAsync(
                context.TargetStore,
                context.TargetDescriptor.Layout,
                targetFolders,
                desiredFolderPaths,
                cancellationToken);

            await _targetReconciler.MoveRemainingTargetObjectsToHistoryAsync(
                context.TargetStore,
                historizer,
                context.TargetDescriptor.Layout,
                targetFolders,
                desiredFolderPaths,
                previousManifestEntries,
                summary,
                writeChanges,
                verifyOnly,
                EnsureChangeManifestInvalidatedAsync,
                cancellationToken);
        }

        if (writeChanges && sourceReadFailures.Count == 0)
        {
            if (rootDescriptorNeedsWrite)
            {
                await EnsureChangeManifestInvalidatedAsync(cancellationToken);
                if (rootDescriptorExists)
                {
                    await (historizer ??
                        throw new YabtSyncException(
                            "A mutating backup requires destination historization."))
                        .MoveRootObjectAsync(
                            BackupRootFileNames.Primary,
                            "Backup",
                            cancellationToken);
                    _logger.LogControlMetadataOperation(
                        "Moved to history (replacement)",
                        BackupRootFileNames.Primary);
                }

                await _rootDocuments.UploadRootDocumentAsync(
                    context.TargetStore,
                    context.SourceDocument ??
                        throw new YabtSyncException(
                            "A root descriptor write requires preserved source bytes."),
                    cancellationToken);
            }

            using var nextChangeManifest = _changeManifestSerializer.Create(
                nextManifestEntries.Values,
                context.Policy.Format,
                context.SourceDocument?.ContentHash,
                context.SourceDocument?.ContentLength);
            await (manifestTransaction ??
                throw new YabtSyncException("A mutating backup requires a manifest transaction."))
                .PublishAsync(nextChangeManifest, cancellationToken);

            if (historizer is not null)
            {
                await historizer.CompleteAsync(cancellationToken);
            }
        }

        sourceReadFailures = SyncFailureReporting.NormalizeFailures(sourceReadFailures);
        if (sourceReadFailures.Count != 0)
        {
            _logger.LogArchiveSyncIncomplete(operationName, sourceReadFailures.Count);
            return new SyncRunResult
            (
                Completed: false,
                Message: SyncFailureReporting.BuildIncompleteReadMessage(
                    $"Archive {operationName}",
                    sourceReadFailures,
                    writeChanges ?
                        "Readable objects may have been archived as rerun checkpoints and " +
                            "blocking entries may have been moved to history. Unrelated " +
                            "removals and root descriptor replacement were deferred; no " +
                            "successful live change manifest was published." :
                        "No changes were made."),
                NewCount: summary.NewCount,
                ChangedCount: summary.ChangedCount,
                ExtraCount: summary.ExtraCount,
                UnchangedCount: summary.UnchangedCount,
                Failures: sourceReadFailures
            );
        }

        _logger.LogArchiveSyncCompleted(
            operationName,
            summary.NewCount,
            summary.ChangedCount,
            summary.ExtraCount,
            summary.UnchangedCount);

        var completed = !verifyOnly ||
            summary.NewCount == 0 &&
            summary.ChangedCount == 0 &&
            summary.ExtraCount == 0 &&
            !rootDescriptorNeedsWrite;
        var message = verifyOnly && rootDescriptorNeedsWrite ?
            $"Archive {operationName} did not verify, although " +
                $"{summary.UnchangedCount} live object(s) appear unchanged." :
            BuildSummaryMessage(
                operationName,
                summary,
                verifyOnly,
                byteForByte);
        if (rootDescriptorNeedsWrite)
        {
            var descriptorState = rootDescriptorExists ?
                "differs from the source descriptor" :
                "is missing";
            if (writeChanges)
            {
                message += rootDescriptorExists ?
                    $" The target root descriptor {descriptorState} and was replaced." :
                    " The missing target root descriptor was installed.";
            }
            else if (verifyOnly)
            {
                message += $" The target root descriptor {descriptorState}.";
            }
            else
            {
                message += $" The target root descriptor {descriptorState}; a mutating backup " +
                    "would install the exact source bytes.";
            }
        }

        return new
        (
            completed,
            message,
            summary.NewCount,
            summary.ChangedCount,
            summary.ExtraCount,
            summary.UnchangedCount
        );
    }

    private static string BuildSummaryMessage
    (
        string operationName,
        ArchiveSyncSummary summary,
        bool verifyOnly,
        bool byteForByte
    )
    {
        if (verifyOnly &&
            summary.NewCount == 0 &&
            summary.ChangedCount == 0 &&
            summary.ExtraCount == 0)
        {
            return byteForByte ?
                $"Archive {operationName} completed byte-for-byte; " +
                    $"verified {summary.UnchangedCount} unchanged object(s)." :
                $"Archive {operationName} quick check completed from metadata fingerprints; " +
                    $"{summary.UnchangedCount} object(s) appear unchanged. " +
                    "Use --byte-for-byte for a full content comparison.";
        }

        if (verifyOnly && !byteForByte)
        {
            operationName += " quick metadata check";
        }

        return $"Archive {operationName} completed; {summary.NewCount} new object(s), " +
            $"{summary.ChangedCount} changed object(s), {summary.ExtraCount} extra object(s), " +
            $"and {summary.UnchangedCount} unchanged object(s).";
    }
}

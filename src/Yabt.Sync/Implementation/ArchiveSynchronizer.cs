using System.Buffers;
using System.Collections.Frozen;
using System.IO.Compression;
using System.IO.Hashing;
using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class ArchiveSynchronizer
(
    ILogger<ArchiveSynchronizer> _logger,
    IBackupRootLocator _backupRootLocator,
    IFolderPolicyReader _folderPolicyReader,
    IEnumerable<IArchiveFormatProjector> projectors,
    IEnumerable<IBackupRootStoreResolver> storeResolvers,
    IEnumerable<ISourceRootObjectStoreResolver> sourceRootObjectStoreResolvers,
    IChangeManifestSerializer _changeManifestSerializer,
    TimeProvider _timeProvider
) : IArchiveSynchronizer
{
    private const int DefaultBufferSize = 81_920;

    private readonly FrozenDictionary<string, IArchiveFormatProjector> _projectors = projectors.ToFrozenDictionary
    (
        projector => projector.FormatName,
        StringComparer.Ordinal
    );

    private readonly FrozenDictionary<string, IBackupRootStoreResolver> _storeResolvers = storeResolvers.ToFrozenDictionary
    (
        resolver => resolver.StoreKind,
        StringComparer.Ordinal
    );

    private readonly ISourceRootObjectStoreResolver _sourceRootObjectStoreResolver =
        sourceRootObjectStoreResolvers.SingleOrDefault() ??
        throw new YabtSyncException("Exactly one source root object store resolver must be registered.");

    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new Dictionary<string, string>(StringComparer.Ordinal).ToFrozenDictionary(StringComparer.Ordinal);

    public async Task<SyncRunResult> SyncAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(SyncAsync));

        _logger.LogSyncRequested(
            request.SourceRoot,
            request.DryRun);

        var context = await CreateContextAsync(request, cancellationToken);

        return await ApplyProjectionAsync(
            context,
            writeChanges: !request.DryRun,
            verifyOnly: false,
            byteForByte: request.ByteForByte,
            operationName: request.DryRun ? "sync dry run" : "sync",
            cancellationToken);
    }

    public Task<SyncRunResult> RestoreAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(RestoreAsync));

        _ = request;
        cancellationToken.ThrowIfCancellationRequested();

        //TODO: Define restore source selection, especially how a user chooses live versus a historical version.
        return Task.FromResult(new SyncRunResult
        (
            Completed: false,
            Message: "Restore command wiring is implemented, but restore semantics are still intentionally open."
        ));
    }

    public Task<SyncRunResult> ScanAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ScanAsync));

        _ = request;
        cancellationToken.ThrowIfCancellationRequested();

        //TODO: Add scalable change detection abstractions before implementing real scan output.
        return Task.FromResult(new SyncRunResult
        (
            Completed: true,
            Message: "Scan completed as a no-op placeholder; change detection is not implemented yet."
        ));
    }

    public async Task<SyncRunResult> VerifyAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(VerifyAsync));

        var context = await CreateContextAsync(request, cancellationToken);

        return await ApplyProjectionAsync(
            context,
            writeChanges: false,
            verifyOnly: true,
            byteForByte: request.ByteForByte,
            operationName: "verify",
            cancellationToken);
    }

    public Task<SyncRunResult> PackAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(PackAsync));

        _ = request;
        cancellationToken.ThrowIfCancellationRequested();

        //TODO: Decide whether pack writes package artifacts locally, to a target store, or only previews projection output.
        return Task.FromResult(new SyncRunResult
        (
            Completed: false,
            Message: "Pack command wiring is implemented, but standalone pack semantics are still intentionally open."
        ));
    }

    public Task<SyncRunResult> ReconcileAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ReconcileAsync));

        _ = request;
        cancellationToken.ThrowIfCancellationRequested();

        //TODO: Define bidirectional reconciliation conflict handling before mutating either side.
        return Task.FromResult(new SyncRunResult
        (
            Completed: false,
            Message: "Reconcile command wiring is implemented, but conflict semantics are still intentionally open."
        ));
    }

    private async Task<ArchiveSyncContext> CreateContextAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);

        var sourceRootPath = Path.GetFullPath(request.SourceRoot);
        var sourceLocation = await _backupRootLocator.LocateRootAsync(
            sourceRootPath,
            cancellationToken);
        var sourceDescriptor = sourceLocation.Descriptor;
        ValidateInternalLayoutPrefixes(sourceDescriptor.Layout);
        var targetStoreConfiguration = GetTargetStoreConfiguration(
            sourceDescriptor,
            request.TargetStoreId);

        if (!_storeResolvers.TryGetValue(targetStoreConfiguration.Kind, out var targetStoreResolver))
        {
            throw new YabtSyncException(
                $"No object store resolver is registered for store kind '{targetStoreConfiguration.Kind}'.");
        }

        var policy = await _folderPolicyReader.ReadPolicyAsync(
            sourceRootPath,
            cancellationToken);
        if (!_projectors.TryGetValue(policy.Format, out var projector))
        {
            throw new YabtSyncException($"No archive format projector is registered for format '{policy.Format}'.");
        }

        var sourceStore = _sourceRootObjectStoreResolver.ResolveSourceRoot(sourceLocation.RootPath);
        var targetStore = targetStoreResolver.ResolveStore(
            targetStoreConfiguration,
            sourceLocation.RootPath);

        return new
        (
            sourceRootPath,
            CreateSourcePrefix(sourceRootPath, sourceLocation),
            sourceStore,
            targetStore,
            sourceDescriptor,
            sourceDescriptor,
            policy,
            projector
        );
    }

    private BackupRootStore GetTargetStoreConfiguration
    (
        BackupRootDescriptor descriptor,
        string? requestedStoreId
    )
    {
        if (descriptor.Stores is null)
        {
            throw new YabtSyncException("Backup root descriptor does not define any target stores.");
        }

        var effectiveStoreId = string.IsNullOrWhiteSpace(requestedStoreId) ?
            descriptor.DefaultStoreId :
            requestedStoreId;
        if (!string.IsNullOrWhiteSpace(effectiveStoreId))
        {
            foreach (var store in descriptor.Stores)
            {
                if (string.Equals(store.Id, effectiveStoreId, StringComparison.OrdinalIgnoreCase))
                {
                    return store;
                }
            }

            throw new YabtSyncException(
                $"Backup root descriptor does not define target store '{effectiveStoreId}'.");
        }

        BackupRootStore? firstStore = null;
        var hasMultipleStores = false;
        foreach (var store in descriptor.Stores)
        {
            if (firstStore is null)
            {
                firstStore = store;
                continue;
            }

            hasMultipleStores = true;
        }

        if (firstStore is null)
        {
            throw new YabtSyncException("Backup root descriptor does not define any target stores.");
        }

        if (hasMultipleStores)
        {
            _logger.LogMultipleTargetStoresWithoutSelection(
                descriptor.ArchiveId,
                firstStore.Id);
        }

        return firstStore;
    }

    private static string? CreateSourcePrefix
    (
        string sourceRootPath,
        BackupRootLocation sourceLocation
    )
    {
        var relativePath = ToArchiveRelativePath(
            Path.GetRelativePath(sourceLocation.RootPath, sourceRootPath));
        var livePrefix = ArchiveLayout.NormalizeObjectKey(sourceLocation.Descriptor.Layout.LivePrefix);

        if (string.IsNullOrEmpty(relativePath))
        {
            return livePrefix;
        }

        if (string.IsNullOrEmpty(livePrefix) ||
            ArchiveLayout.IsUnderPrefix(relativePath, livePrefix))
        {
            return relativePath;
        }

        return ArchiveLayout.CombinePrefixAndRelativePath(livePrefix, relativePath);
    }

    private static string ToArchiveRelativePath(string relativePath)
    {
        if (string.Equals(relativePath, ".", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return ArchiveLayout.NormalizeObjectKey(relativePath);
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
        await context.SourceStore.EnsureReadyAsync(cancellationToken);
        await context.TargetStore.EnsureReadyAsync(cancellationToken);

        var changeManifestFileName = GetChangeManifestFileName(
            context.TargetDescriptor.ChangeManifestCompression);
        var changeManifestLoad = await ReadChangeManifestAsync(
            context.TargetStore,
            recoverInvalidManifest: writeChanges || byteForByte,
            cancellationToken);
        var previousChangeManifest = changeManifestLoad.Manifest;
        var previousManifestEntries = previousChangeManifest?.Entries.ToDictionary(
            entry => entry.RelativePath,
            StringComparer.Ordinal) ??
            new Dictionary<string, ArchiveChangeManifestEntry>(StringComparer.Ordinal);
        var nextManifestEntries = new Dictionary<string, ArchiveChangeManifestEntry>(StringComparer.Ordinal);

        var targetFolders = new Dictionary<string, TargetFolderState>(StringComparer.Ordinal);
        var desiredFolderPaths = new HashSet<string>(StringComparer.Ordinal);
        await LoadTargetFolderStateAsync(
            context.TargetStore,
            context.TargetDescriptor.Layout,
            targetFolders,
            string.Empty,
            cancellationToken);

        var projectedObjects = context.Projector.ProjectAsync(
            CreateProjectionRequest(context),
            cancellationToken);
        var summary = new ArchiveSyncSummary();
        var historyKeyAllocator = new ArchiveHistoryKeyAllocator(
            context.TargetStore,
            context.TargetDescriptor.Layout,
            _timeProvider.GetUtcNow());
        var changeManifestInvalidated = false;
        var changeManifestInvalidationMarkerActive =
            changeManifestLoad.InvalidationMarkerExists;

        async Task EnsureChangeManifestInvalidatedAsync(CancellationToken currentCancellationToken)
        {
            if (!changeManifestLoad.Exists || changeManifestInvalidated)
            {
                return;
            }

            if (changeManifestLoad.RequiresInvalidationMarker &&
                !changeManifestInvalidationMarkerActive)
            {
                await UploadChangeManifestInvalidationMarkerAsync(
                    context.TargetStore,
                    currentCancellationToken);
                changeManifestInvalidationMarkerActive = true;
            }

            await MoveChangeManifestsToHistoryAsync(
                context.TargetStore,
                historyKeyAllocator,
                changeManifestLoad.ExistingFileNames,
                currentCancellationToken);
            changeManifestInvalidated = true;
        }

        await foreach (var projectedObject in projectedObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = ArchiveLayout.NormalizeObjectKey(projectedObject.RelativePath);
            AddDesiredFolderPaths(relativePath, desiredFolderPaths);
            var targetFolder = await LoadTargetFolderStateAsync(
                context.TargetStore,
                context.TargetDescriptor.Layout,
                targetFolders,
                GetParentPrefix(relativePath),
                cancellationToken);

            if (!targetFolder.Objects.Remove(relativePath, out var targetObject) &&
                IsEmptyFolderMarker(relativePath) &&
                await context.TargetStore.ExistsAsync(
                    context.TargetDescriptor.Layout.ToLiveObjectKey(relativePath),
                    cancellationToken))
            {
                targetObject = new(context.TargetDescriptor.Layout.ToLiveObjectKey(relativePath));
            }

            if (targetObject is not null)
            {
                previousManifestEntries.TryGetValue(relativePath, out var previousManifestEntry);
                var comparison = await CompareProjectedObjectAsync(
                        projectedObject,
                        context.TargetStore,
                        targetObject,
                        previousManifestEntry,
                        byteForByte,
                        prepareChangedContent: writeChanges,
                        cancellationToken);
                await using var preparedContent = comparison.PreparedContent;
                if (comparison.Same)
                {
                    summary.AddUnchanged();
                    nextManifestEntries.Add(
                        relativePath,
                        comparison.ManifestEntry ??
                            throw new YabtSyncException(
                                $"Content comparison for '{relativePath}' did not produce manifest evidence."));
                    continue;
                }

                summary.AddChanged();
                if (writeChanges)
                {
                    await EnsureChangeManifestInvalidatedAsync(cancellationToken);

                    await MoveTargetObjectToHistoryAsync(
                        context.TargetStore,
                        historyKeyAllocator,
                        context.TargetDescriptor.Layout,
                        relativePath,
                        cancellationToken);

                    var manifestEntry = await UploadProjectedObjectAsync(
                        context.TargetStore,
                        context.TargetDescriptor.Layout,
                        projectedObject,
                        relativePath,
                        cancellationToken,
                        preparedContent);
                    nextManifestEntries.Add(relativePath, manifestEntry);
                }

                continue;
            }

            summary.AddNew();
            if (writeChanges)
            {
                await EnsureChangeManifestInvalidatedAsync(cancellationToken);

                var manifestEntry = await UploadProjectedObjectAsync(
                    context.TargetStore,
                    context.TargetDescriptor.Layout,
                    projectedObject,
                    relativePath,
                    cancellationToken);
                nextManifestEntries.Add(relativePath, manifestEntry);
            }
        }

        await ReconcileDesiredTargetStateAsync(
            context.TargetStore,
            context.TargetDescriptor.Layout,
            targetFolders,
            desiredFolderPaths,
            cancellationToken);

        await MoveRemainingTargetObjectsToHistoryAsync(
            context.TargetStore,
            historyKeyAllocator,
            context.TargetDescriptor.Layout,
            targetFolders,
            summary,
            writeChanges,
            EnsureChangeManifestInvalidatedAsync,
            cancellationToken);

        if (writeChanges)
        {
            var nextChangeManifest = _changeManifestSerializer.Create(nextManifestEntries.Values);
            var changeManifestNeedsWrite = changeManifestInvalidated ||
                changeManifestLoad.NeedsRepresentationRewrite(changeManifestFileName) ||
                previousChangeManifest is null ||
                !string.Equals(
                    previousChangeManifest.ManifestHash,
                    nextChangeManifest.ManifestHash,
                    StringComparison.Ordinal);
            if (changeManifestNeedsWrite)
            {
                await EnsureChangeManifestInvalidatedAsync(cancellationToken);
                await UploadChangeManifestAsync(
                    context.TargetStore,
                    nextChangeManifest,
                    changeManifestFileName,
                    cancellationToken);
                if (changeManifestInvalidationMarkerActive)
                {
                    await MoveChangeManifestInvalidationMarkerToHistoryAsync(
                        context.TargetStore,
                        historyKeyAllocator,
                        cancellationToken);
                    changeManifestInvalidationMarkerActive = false;
                }
            }
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
            summary.ExtraCount == 0;
        var message = BuildSummaryMessage(
            operationName,
            summary,
            verifyOnly,
            byteForByte);

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

    private ArchiveProjectionRequest CreateProjectionRequest(ArchiveSyncContext context)
    {
        var filteredSourceStore = new ArchiveFilteredObjectStore
        (
            context.SourceStore,
            CreateInternalObjectKeys(context.SourceDescriptor.Layout),
            CreateInternalObjectPrefixes(context.SourceDescriptor.Layout)
        );
        var projectionSourceStore = new ArchiveProjectionObjectStore
        (
            filteredSourceStore,
            context.SourceRoot,
            context.SourcePrefix,
            _folderPolicyReader,
            _projectors
        );

        return new
        (
            projectionSourceStore,
            context.SourcePrefix,
            context.Policy,
            context.SourceRoot
        );
    }

    private static async Task<TargetFolderState> LoadTargetFolderStateAsync
    (
        IObjectStore targetStore,
        ArchiveLayout targetLayout,
        Dictionary<string, TargetFolderState> targetFolders,
        string relativeFolderPrefix,
        CancellationToken cancellationToken
    )
    {
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
        var folderItems = targetStore.GetFolderItemsAsync(
            targetFolderPrefix,
            recursive: false,
            cancellationToken);

        await foreach (var folderItem in folderItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetKey = ArchiveLayout.NormalizeObjectKey(folderItem.Key);
            if (IsInternalObject(targetKey, targetLayout))
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
        var parentPrefix = GetParentPrefix(relativeFolderPrefix);
        if (targetFolders.TryGetValue(parentPrefix, out var parentFolder))
        {
            parentFolder.Folders.Remove(relativeFolderPrefix);
        }
    }

    private static async Task ReconcileDesiredTargetStateAsync
    (
        IObjectStore targetStore,
        ArchiveLayout targetLayout,
        Dictionary<string, TargetFolderState> targetFolders,
        IReadOnlySet<string> desiredFolderPaths,
        CancellationToken cancellationToken
    )
    {
        var orderedDesiredFolderPaths = desiredFolderPaths
            .OrderBy(GetPathDepth)
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
            var parentPrefix = GetParentPrefix(desiredFolderPath);
            if (targetFolders.TryGetValue(parentPrefix, out var parentFolder))
            {
                parentFolder.Folders.Remove(desiredFolderPath);
            }
        }
    }

    private static async Task MoveRemainingTargetObjectsToHistoryAsync
    (
        IObjectStore targetStore,
        ArchiveHistoryKeyAllocator historyKeyAllocator,
        ArchiveLayout targetLayout,
        Dictionary<string, TargetFolderState> targetFolders,
        ArchiveSyncSummary summary,
        bool writeChanges,
        Func<CancellationToken, Task> beforeFirstWriteAsync,
        CancellationToken cancellationToken
    )
    {
        var extraFolderPaths = targetFolders.Values
            .SelectMany(targetFolder => targetFolder.Folders.Keys)
            .Distinct(StringComparer.Ordinal);
        var topLevelExtraFolderPaths = SelectTopLevelFolderPaths(extraFolderPaths);

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
                if (writeChanges)
                {
                    await beforeFirstWriteAsync(cancellationToken);

                    await MoveTargetObjectToHistoryAsync(
                        targetStore,
                        historyKeyAllocator,
                        targetLayout,
                        relativePath,
                        cancellationToken);
                }
            }
        }

        foreach (var relativeFolderPath in topLevelExtraFolderPaths)
        {
            await MoveTargetFolderToHistoryAsync(
                targetStore,
                historyKeyAllocator,
                targetLayout,
                relativeFolderPath,
                summary,
                writeChanges,
                beforeFirstWriteAsync,
                cancellationToken);
        }
    }

    private static async Task MoveTargetFolderToHistoryAsync
    (
        IObjectStore targetStore,
        ArchiveHistoryKeyAllocator historyKeyAllocator,
        ArchiveLayout targetLayout,
        string relativeFolderPath,
        ArchiveSyncSummary summary,
        bool writeChanges,
        Func<CancellationToken, Task> beforeWriteAsync,
        CancellationToken cancellationToken
    )
    {
        var targetFolderPrefix = targetLayout.ToLiveObjectKey(relativeFolderPath);
        var targetItems = targetStore.GetFolderItemsAsync(
            targetFolderPrefix,
            recursive: true,
            cancellationToken);
        var livePrefix = ArchiveLayout.NormalizeObjectPrefix(targetLayout.LivePrefix);
        var visibleObjectCount = 0;

        await foreach (var targetItem in targetItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (targetItem.Object is null)
            {
                continue;
            }

            var targetObject = targetItem.Object;
            var targetKey = ArchiveLayout.NormalizeObjectKey(targetObject.Key);
            if (IsInternalObject(targetKey, targetLayout))
            {
                continue;
            }

            var relativePath = ArchiveLayout.RemovePrefix(targetKey, livePrefix);
            if (string.IsNullOrEmpty(relativePath))
            {
                continue;
            }

            visibleObjectCount++;
            summary.AddExtra();
        }

        if (visibleObjectCount == 0)
        {
            summary.AddExtra();
        }

        if (!writeChanges)
        {
            return;
        }

        await beforeWriteAsync(cancellationToken);

        var destinationFolderPrefix = await historyKeyAllocator.CreateHistoricalKeyAsync(
            relativeFolderPath,
            cancellationToken);

        try
        {
            await targetStore.MoveFolderAsync(
                targetFolderPrefix,
                destinationFolderPrefix,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Sync history move failed for target folder '{targetFolderPrefix}' to '{destinationFolderPrefix}'.",
                ex);
        }
    }

    private static async Task<ArchiveChangeManifestEntry> UploadProjectedObjectAsync
    (
        IObjectStore targetStore,
        ArchiveLayout targetLayout,
        ArchiveProjectedObject projectedObject,
        string relativePath,
        CancellationToken cancellationToken,
        ArchiveObjectContent? preparedContent = default
    )
    {
        var targetKey = targetLayout.ToLiveObjectKey(relativePath);
        ArchiveObjectContent? openedContent = null;
        try
        {
            try
            {
                var content = preparedContent;
                if (content is null)
                {
                    openedContent = await projectedObject.OpenContentAsync(cancellationToken);
                    content = openedContent;
                }

                using var hashingContent = new ContentHashingReadStream(content.Content);

                await targetStore.UploadAsync(
                    targetKey,
                    hashingContent,
                    content.ContentType,
                    content.Metadata ?? EmptyMetadata,
                    cancellationToken);

                var endProbe = new byte[1];
                var unreadByteCount = await hashingContent.ReadAsync(
                    endProbe,
                    cancellationToken);
                if (unreadByteCount != 0)
                {
                    throw new InvalidDataException(
                        $"Target upload for '{targetKey}' completed before consuming all projected content.");
                }

                return CreateManifestEntry(
                    relativePath,
                    projectedObject,
                    hashingContent.BytesRead,
                    hashingContent.CompleteHash());
            }
            finally
            {
                if (openedContent is not null)
                {
                    await openedContent.DisposeAsync();
                }
            }
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Sync upload failed for projected object '{relativePath}' to target object '{targetKey}'.",
                ex);
        }
    }

    private async Task<ChangeManifestLoad> ReadChangeManifestAsync
    (
        IObjectStore targetStore,
        bool recoverInvalidManifest,
        CancellationToken cancellationToken
    )
    {
        var existingFileNames = new List<string>(2);
        foreach (var fileName in GetChangeManifestFileNames())
        {
            if (await targetStore.ExistsAsync(fileName, cancellationToken))
            {
                existingFileNames.Add(fileName);
            }
        }

        var invalidationMarkerExists = await targetStore.ExistsAsync(
            ArchiveChangeManifest.InvalidationMarkerFileName,
            cancellationToken);
        if (existingFileNames.Count == 0 && !invalidationMarkerExists)
        {
            return new(existingFileNames, false, null);
        }

        try
        {
            if (invalidationMarkerExists)
            {
                throw new InvalidDataException(
                    "A prior change-manifest replacement did not complete.");
            }

            ArchiveChangeManifest? selectedManifest = null;
            foreach (var fileName in existingFileNames)
            {
                var manifest = await ReadChangeManifestFileAsync(
                    targetStore,
                    fileName,
                    cancellationToken);
                if (selectedManifest is not null &&
                    !string.Equals(
                        selectedManifest.ManifestHash,
                        manifest.ManifestHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The compressed and uncompressed change manifests describe different live states.");
                }

                selectedManifest = manifest;
            }

            return new(existingFileNames, false, selectedManifest);
        }
        catch (Exception ex)
        {
            var evidenceFileNames = invalidationMarkerExists ?
                existingFileNames.Append(ArchiveChangeManifest.InvalidationMarkerFileName) :
                existingFileNames;
            var manifestNames = string.Join("', '", evidenceFileNames);
            if (recoverInvalidManifest)
            {
                _logger.LogInvalidChangeManifestIgnored(
                    manifestNames,
                    ex);
                return new(existingFileNames, invalidationMarkerExists, null);
            }

            throw new YabtSyncException(
                $"Change manifest state containing '{manifestNames}' could not be read or validated.",
                ex);
        }
    }

    private async Task<ArchiveChangeManifest> ReadChangeManifestFileAsync
    (
        IObjectStore targetStore,
        string fileName,
        CancellationToken cancellationToken
    )
    {
        await using var content = await targetStore.OpenReadAsync(
            fileName,
            cancellationToken);
        if (!string.Equals(
                fileName,
                ArchiveChangeManifest.BrotliFileName,
                StringComparison.Ordinal))
        {
            return await _changeManifestSerializer.ReadAsync(
                content.Content,
                cancellationToken);
        }

        await using var decompressedContent = new BrotliStream(
            content.Content,
            CompressionMode.Decompress,
            leaveOpen: true);
        return await _changeManifestSerializer.ReadAsync(
            decompressedContent,
            cancellationToken);
    }

    private async Task UploadChangeManifestAsync
    (
        IObjectStore targetStore,
        ArchiveChangeManifest manifest,
        string fileName,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var content = new MemoryStream();
            if (string.Equals(
                    fileName,
                    ArchiveChangeManifest.BrotliFileName,
                    StringComparison.Ordinal))
            {
                await using (var compressedContent = new BrotliStream(
                    content,
                    CompressionLevel.Optimal,
                    leaveOpen: true))
                {
                    await _changeManifestSerializer.WriteAsync(
                        manifest,
                        compressedContent,
                        cancellationToken);
                }
            }
            else
            {
                await _changeManifestSerializer.WriteAsync(
                    manifest,
                    content,
                    cancellationToken);
            }

            content.Position = 0;

            await targetStore.UploadAsync(
                fileName,
                content,
                string.Equals(
                    fileName,
                    ArchiveChangeManifest.BrotliFileName,
                    StringComparison.Ordinal) ?
                        "application/octet-stream" :
                        "application/json",
                EmptyMetadata,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Change manifest '{fileName}' could not be uploaded.",
                ex);
        }
    }

    private static async Task UploadChangeManifestInvalidationMarkerAsync
    (
        IObjectStore targetStore,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using var content = new MemoryStream([], writable: false);
            await targetStore.UploadAsync(
                ArchiveChangeManifest.InvalidationMarkerFileName,
                content,
                "application/octet-stream",
                EmptyMetadata,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Change manifest invalidation marker " +
                    $"'{ArchiveChangeManifest.InvalidationMarkerFileName}' could not be uploaded.",
                ex);
        }
    }

    private static async Task MoveChangeManifestsToHistoryAsync
    (
        IObjectStore targetStore,
        ArchiveHistoryKeyAllocator historyKeyAllocator,
        IEnumerable<string> fileNames,
        CancellationToken cancellationToken
    )
    {
        foreach (var fileName in fileNames)
        {
            var destinationKey = await historyKeyAllocator.CreateHistoricalKeyAsync(
                fileName,
                cancellationToken);

            try
            {
                await targetStore.MoveAsync(
                    fileName,
                    destinationKey,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                throw new YabtSyncException(
                    $"Change manifest history move failed from '{fileName}' " +
                        $"to '{destinationKey}'.",
                    ex);
            }
        }
    }

    private static async Task MoveChangeManifestInvalidationMarkerToHistoryAsync
    (
        IObjectStore targetStore,
        ArchiveHistoryKeyAllocator historyKeyAllocator,
        CancellationToken cancellationToken
    )
    {
        var destinationKey = await historyKeyAllocator.CreateHistoricalKeyAsync(
            ArchiveChangeManifest.InvalidationMarkerFileName,
            cancellationToken);
        try
        {
            await targetStore.MoveAsync(
                ArchiveChangeManifest.InvalidationMarkerFileName,
                destinationKey,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Change manifest invalidation marker history move failed from " +
                    $"'{ArchiveChangeManifest.InvalidationMarkerFileName}' to '{destinationKey}'.",
                ex);
        }
    }

    private static async Task MoveTargetObjectToHistoryAsync
    (
        IObjectStore targetStore,
        ArchiveHistoryKeyAllocator historyKeyAllocator,
        ArchiveLayout targetLayout,
        string relativePath,
        CancellationToken cancellationToken
    )
    {
        var sourceKey = targetLayout.ToLiveObjectKey(relativePath);
        var destinationKey = await historyKeyAllocator.CreateHistoricalKeyAsync(
            relativePath,
            cancellationToken);

        try
        {
            await targetStore.MoveAsync(sourceKey, destinationKey, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Sync history move failed for target object '{sourceKey}' to '{destinationKey}'.",
                ex);
        }
    }

    private static async Task<ProjectedObjectComparison> CompareProjectedObjectAsync
    (
        ArchiveProjectedObject projectedObject,
        IObjectStore targetStore,
        ArchiveObjectInfo targetObject,
        ArchiveChangeManifestEntry? previousManifestEntry,
        bool byteForByte,
        bool prepareChangedContent,
        CancellationToken cancellationToken
    )
    {
        var expectedArtifactLength = projectedObject.ContentLength ??
            (byteForByte ? null : previousManifestEntry?.ArtifactLength);
        if (expectedArtifactLength.HasValue &&
            targetObject.ContentLength.HasValue &&
            expectedArtifactLength.Value != targetObject.ContentLength.Value)
        {
            return new(false, null, null);
        }

        if (!byteForByte &&
            TryCreateFastManifestEntry(
                projectedObject,
                targetObject,
                previousManifestEntry,
                out var fastManifestEntry))
        {
            return new(true, fastManifestEntry, null);
        }

        ArchiveObjectContent? sourceContent = null;
        FileStream? replayContent = null;
        try
        {
            try
            {
                sourceContent = await projectedObject.OpenContentAsync(cancellationToken);
                if (prepareChangedContent)
                {
                    replayContent = CreateComparisonReplayStream();
                }

                StreamComparison streamComparison;
                await using (var targetContent = await targetStore.OpenReadAsync(
                    targetObject.Key,
                    cancellationToken))
                {
                    streamComparison = await CompareStreamsAsync(
                        sourceContent.Content,
                        targetContent.Content,
                        DefaultBufferSize,
                        replayContent,
                        cancellationToken);
                }

                if (!streamComparison.Same)
                {
                    if (replayContent is null)
                    {
                        return new(false, null, null);
                    }

                    await sourceContent.Content.CopyToAsync(
                        replayContent,
                        DefaultBufferSize,
                        cancellationToken);
                    await replayContent.FlushAsync(cancellationToken);
                    if (projectedObject.ContentLength.HasValue &&
                        projectedObject.ContentLength.Value != replayContent.Length)
                    {
                        throw new YabtSyncException(
                            $"Projected object '{projectedObject.RelativePath}' reported length " +
                            $"{projectedObject.ContentLength.Value}, but its content contained " +
                            $"{replayContent.Length} bytes.");
                    }

                    replayContent.Position = 0;

                    var preparedContentType = sourceContent.ContentType;
                    var preparedContentMetadata = sourceContent.Metadata;
                    var completedSourceContent = sourceContent;
                    sourceContent = null;
                    await completedSourceContent.DisposeAsync();

                    var preparedContent = new ArchiveObjectContent
                    (
                        replayContent,
                        preparedContentType,
                        preparedContentMetadata
                    );
                    replayContent = null;

                    return new(false, null, preparedContent);
                }

                return new
                (
                    true,
                    CreateManifestEntry(
                        ArchiveLayout.NormalizeObjectKey(projectedObject.RelativePath),
                        projectedObject,
                        streamComparison.SourceLength,
                        streamComparison.SourceContentHash ??
                            throw new YabtSyncException(
                                "Successful byte comparison did not produce a content hash.")),
                    null
                );
            }
            finally
            {
                try
                {
                    if (sourceContent is not null)
                    {
                        await sourceContent.DisposeAsync();
                    }
                }
                finally
                {
                    if (replayContent is not null)
                    {
                        await replayContent.DisposeAsync();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Sync content comparison failed for target object '{targetObject.Key}'.",
                ex);
        }
    }

    private static bool TryCreateFastManifestEntry
    (
        ArchiveProjectedObject projectedObject,
        ArchiveObjectInfo targetObject,
        ArchiveChangeManifestEntry? previousManifestEntry,
        out ArchiveChangeManifestEntry? manifestEntry
    )
    {
        // A matching metadata fingerprint is a quick presumption, not byte-level verification.
        // --byte-for-byte bypasses this method, and incomplete target evidence falls back to streams.
        manifestEntry = null;
        if (previousManifestEntry is null ||
            string.IsNullOrWhiteSpace(projectedObject.ChangeFingerprint) ||
            string.IsNullOrWhiteSpace(previousManifestEntry.ContentHash) ||
            !ArchiveHash.IsValid(previousManifestEntry.ContentHash) ||
            !string.Equals(
                projectedObject.ChangeFingerprint,
                previousManifestEntry.ChangeFingerprint,
                StringComparison.Ordinal))
        {
            return false;
        }

        var expectedArtifactLength = projectedObject.ContentLength ??
            previousManifestEntry.ArtifactLength;
        if (!expectedArtifactLength.HasValue)
        {
            return false;
        }

        if (!targetObject.ContentLength.HasValue)
        {
            if (!IsEmptyFolderMarker(projectedObject.RelativePath))
            {
                return false;
            }
        }
        else if (targetObject.ContentLength.Value != expectedArtifactLength.Value)
        {
            return false;
        }

        if (HaveSameHashAlgorithm(
                targetObject.ContentHash,
                previousManifestEntry.ContentHash) &&
            !string.Equals(
                targetObject.ContentHash,
                previousManifestEntry.ContentHash,
                StringComparison.Ordinal))
        {
            return false;
        }

        manifestEntry = CreateManifestEntry(
            previousManifestEntry.RelativePath,
            projectedObject,
            expectedArtifactLength.Value,
            previousManifestEntry.ContentHash);
        return true;
    }

    private static ArchiveChangeManifestEntry CreateManifestEntry
    (
        string relativePath,
        ArchiveProjectedObject projectedObject,
        long contentLength,
        string contentHash
    )
    {
        if (projectedObject.ContentLength.HasValue &&
            projectedObject.ContentLength.Value != contentLength)
        {
            throw new YabtSyncException(
                $"Projected object '{relativePath}' reported length " +
                $"{projectedObject.ContentLength.Value}, but its content contained {contentLength} bytes.");
        }

        var changeFingerprint = string.IsNullOrWhiteSpace(projectedObject.ChangeFingerprint) ?
            contentHash :
            projectedObject.ChangeFingerprint;
        return new
        (
            ArchiveLayout.NormalizeObjectKey(relativePath),
            changeFingerprint,
            ArtifactLength: projectedObject.ContentLength.HasValue ? null : contentLength,
            ContentHash: contentHash
        );
    }

    private static bool HaveSameHashAlgorithm
    (
        string? firstHash,
        string? secondHash
    )
    {
        if (string.IsNullOrWhiteSpace(firstHash) ||
            string.IsNullOrWhiteSpace(secondHash))
        {
            return false;
        }

        var firstSeparator = firstHash.IndexOf(':', StringComparison.Ordinal);
        var secondSeparator = secondHash.IndexOf(':', StringComparison.Ordinal);
        return firstSeparator > 0 &&
            secondSeparator == firstSeparator &&
            firstHash.AsSpan(0, firstSeparator).SequenceEqual(secondHash.AsSpan(0, secondSeparator));
    }

    private static async Task<StreamComparison> CompareStreamsAsync
    (
        Stream source,
        Stream target,
        int bufferSize,
        Stream? sourceReplay,
        CancellationToken cancellationToken
    )
    {
        var sourceBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var targetBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var sourceHash = new XxHash128();
        long sourceLength = 0;

        try
        {
            while (true)
            {
                var sourceBytesRead = await FillBufferAsync(
                    source,
                    sourceBuffer.AsMemory(0, bufferSize),
                    cancellationToken);
                var targetBytesRead = await FillBufferAsync(
                    target,
                    targetBuffer.AsMemory(0, bufferSize),
                    cancellationToken);

                sourceHash.Append(sourceBuffer.AsSpan(0, sourceBytesRead));
                sourceLength += sourceBytesRead;
                if (sourceReplay is not null && sourceBytesRead != 0)
                {
                    await sourceReplay.WriteAsync(
                        sourceBuffer.AsMemory(0, sourceBytesRead),
                        cancellationToken);
                }

                if (sourceBytesRead != targetBytesRead)
                {
                    return new(false, sourceLength, null);
                }

                if (sourceBytesRead == 0)
                {
                    var hash = sourceHash.GetHashAndReset();
                    return new
                    (
                        true,
                        sourceLength,
                        ArchiveHash.Format(hash)
                    );
                }

                var sourceSpan = sourceBuffer.AsSpan(0, sourceBytesRead);
                var targetSpan = targetBuffer.AsSpan(0, targetBytesRead);
                if (!sourceSpan.SequenceEqual(targetSpan))
                {
                    return new(false, sourceLength, null);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceBuffer);
            ArrayPool<byte>.Shared.Return(targetBuffer);
        }
    }

    private static FileStream CreateComparisonReplayStream()
    {
        // This private local replay is operation-scoped and distinct from a target provider's
        // .yabt-tmp upload staging. DeleteOnClose removes it on every normal disposal path.
        var temporaryPath = Path.Combine(
            Path.GetTempPath(),
            $"yabt-source-replay-{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = DefaultBufferSize,
            Options = FileOptions.Asynchronous |
                FileOptions.SequentialScan |
                FileOptions.DeleteOnClose,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(temporaryPath, options);
    }

    private static async Task<int> FillBufferAsync
    (
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken
    )
    {
        var totalBytesRead = 0;
        while (totalBytesRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer[totalBytesRead..], cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytesRead += bytesRead;
        }

        return totalBytesRead;
    }

    private static IEnumerable<string> CreateInternalObjectKeys(ArchiveLayout layout)
    {
        if (!string.IsNullOrEmpty(ArchiveLayout.NormalizeObjectKey(layout.LivePrefix)))
        {
            return [];
        }

        return
        [
            BackupRootFileNames.Primary,
            ArchiveChangeManifest.UncompressedFileName,
            ArchiveChangeManifest.BrotliFileName,
            ArchiveChangeManifest.InvalidationMarkerFileName,
        ];
    }

    private static IEnumerable<string> GetChangeManifestFileNames() =>
    [
        ArchiveChangeManifest.BrotliFileName,
        ArchiveChangeManifest.UncompressedFileName,
    ];

    private static string GetChangeManifestFileName(string? configuredCompression)
    {
        var effectiveCompression = ArchiveChangeManifestCompression.GetEffective(
            configuredCompression);
        return effectiveCompression switch
        {
            ArchiveChangeManifestCompression.Brotli => ArchiveChangeManifest.BrotliFileName,
            ArchiveChangeManifestCompression.None => ArchiveChangeManifest.UncompressedFileName,
            _ => throw new YabtSyncException(
                $"Unsupported change manifest compression '{effectiveCompression}'."),
        };
    }

    private static List<string> CreateInternalObjectPrefixes(ArchiveLayout layout)
    {
        if (!string.IsNullOrEmpty(ArchiveLayout.NormalizeObjectKey(layout.LivePrefix)))
        {
            return [];
        }

        var prefixes = new List<string>();
        var histPrefix = ArchiveLayout.NormalizeObjectPrefix(layout.HistPrefix);
        if (histPrefix is not null)
        {
            prefixes.Add(histPrefix);
        }

        prefixes.Add(ArchiveInternalFolderNames.TemporaryUploads);
        return prefixes;
    }

    private static void ValidateInternalLayoutPrefixes(ArchiveLayout layout)
    {
        var temporaryPrefix = ArchiveInternalFolderNames.TemporaryUploads;
        var livePrefix = ArchiveLayout.NormalizeObjectPrefix(layout.LivePrefix);
        if (livePrefix is not null && PrefixesOverlap(livePrefix, temporaryPrefix))
        {
            throw new YabtSyncException(
                $"Archive live prefix '{livePrefix}' conflicts with reserved internal prefix " +
                    $"'{temporaryPrefix}'.");
        }

        var histPrefix = ArchiveLayout.NormalizeObjectPrefix(layout.HistPrefix);
        if (histPrefix is not null && PrefixesOverlap(histPrefix, temporaryPrefix))
        {
            throw new YabtSyncException(
                $"Archive history prefix '{histPrefix}' conflicts with reserved internal prefix " +
                    $"'{temporaryPrefix}'.");
        }
    }

    private static bool PrefixesOverlap(string firstPrefix, string secondPrefix) =>
        IsSameOrUnderPrefix(firstPrefix, secondPrefix) ||
        IsSameOrUnderPrefix(secondPrefix, firstPrefix);

    private static bool IsSameOrUnderPrefix(string objectKey, string prefix)
    {
        var normalizedObjectKey = ArchiveLayout.NormalizeObjectKey(objectKey);
        var normalizedPrefix = ArchiveLayout.NormalizeObjectKey(prefix);
        return string.Equals(
                normalizedObjectKey,
                normalizedPrefix,
                StringComparison.OrdinalIgnoreCase) ||
            normalizedObjectKey.StartsWith(
                $"{normalizedPrefix}/",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsInternalObject(string objectKey, ArchiveLayout layout)
    {
        return IsInternalObjectKey(objectKey, CreateInternalObjectKeys(layout)) ||
            IsInternalObjectPrefix(objectKey, CreateInternalObjectPrefixes(layout));
    }

    private static bool IsInternalObjectKey
    (
        string objectKey,
        IEnumerable<string> internalObjectKeys
    )
    {
        foreach (var internalObjectKey in internalObjectKeys)
        {
            if (string.Equals(
                    objectKey,
                    ArchiveLayout.NormalizeObjectKey(internalObjectKey),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInternalObjectPrefix
    (
        string objectKey,
        IEnumerable<string> internalObjectPrefixes
    )
    {
        foreach (var internalObjectPrefix in internalObjectPrefixes)
        {
            var normalizedPrefix = ArchiveLayout.NormalizeObjectPrefix(internalObjectPrefix);
            if (normalizedPrefix is not null &&
                IsSameOrUnderPrefix(objectKey, normalizedPrefix))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddDesiredFolderPaths
    (
        string relativeObjectPath,
        HashSet<string> desiredFolderPaths
    )
    {
        var folderPath = GetParentPrefix(relativeObjectPath);
        while (!string.IsNullOrEmpty(folderPath))
        {
            desiredFolderPaths.Add(folderPath);
            folderPath = GetParentPrefix(folderPath);
        }
    }

    private static List<string> SelectTopLevelFolderPaths(IEnumerable<string> folderPaths)
    {
        var selectedFolderPaths = new List<string>();
        var orderedFolderPaths = folderPaths
            .Select(ArchiveLayout.NormalizeObjectKey)
            .Where(folderPath => !string.IsNullOrEmpty(folderPath))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(GetPathDepth)
            .ThenBy(folderPath => folderPath, StringComparer.Ordinal);

        foreach (var folderPath in orderedFolderPaths)
        {
            if (selectedFolderPaths.Any(
                    selectedFolderPath => ArchiveLayout.IsUnderPrefix(folderPath, selectedFolderPath)))
            {
                continue;
            }

            selectedFolderPaths.Add(folderPath);
        }

        return selectedFolderPaths;
    }

    private static int GetPathDepth(string path) => path.Count(character => character == '/');

    private static string GetParentPrefix(string relativePath)
    {
        var normalizedRelativePath = ArchiveLayout.NormalizeObjectKey(relativePath);
        var separator = normalizedRelativePath.LastIndexOf('/');

        return separator < 0 ? string.Empty : normalizedRelativePath[..separator];
    }

    private static bool IsEmptyFolderMarker(string relativePath)
    {
        var normalizedRelativePath = ArchiveLayout.NormalizeObjectKey(relativePath);
        var separator = normalizedRelativePath.LastIndexOf('/');
        var name = separator < 0 ?
            normalizedRelativePath :
            normalizedRelativePath[(separator + 1)..];

        return string.Equals(
            name,
            ArchiveFolderMarkerFileNames.EmptyFolder,
            StringComparison.Ordinal);
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

    private sealed class TargetFolderState
    {
        public Dictionary<string, ArchiveObjectInfo> Objects { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> Folders { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ProjectedObjectComparison
    (
        bool Same,
        ArchiveChangeManifestEntry? ManifestEntry,
        ArchiveObjectContent? PreparedContent
    );

    private sealed record ChangeManifestLoad
    (
        IReadOnlyList<string> ExistingFileNames,
        bool InvalidationMarkerExists,
        ArchiveChangeManifest? Manifest
    )
    {
        public bool Exists => ExistingFileNames.Count != 0 || InvalidationMarkerExists;

        public bool RequiresInvalidationMarker =>
            InvalidationMarkerExists ||
            Manifest is null && ExistingFileNames.Count > 1;

        public bool NeedsRepresentationRewrite(string expectedFileName) =>
            InvalidationMarkerExists ||
            ExistingFileNames.Count != 1 ||
            !string.Equals(
                ExistingFileNames[0],
                expectedFileName,
                StringComparison.Ordinal);
    }

    private sealed record StreamComparison
    (
        bool Same,
        long SourceLength,
        string? SourceContentHash
    );

}

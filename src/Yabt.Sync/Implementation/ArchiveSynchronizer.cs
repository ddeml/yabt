using System.Buffers;
using System.Collections.Frozen;
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
        string operationName,
        CancellationToken cancellationToken
    )
    {
        await context.SourceStore.EnsureReadyAsync(cancellationToken);
        await context.TargetStore.EnsureReadyAsync(cancellationToken);

        var projection = await context.Projector.ProjectAsync(
            CreateProjectionRequest(context),
            cancellationToken);
        var projectedObjects = projection.Objects
            .OrderBy(candidate => candidate.RelativePath, StringComparer.Ordinal);
        var targetObjects = await ListTargetLiveObjectsAsync(
            context.TargetStore,
            context.TargetDescriptor.Layout,
            cancellationToken);
        var summary = new ArchiveSyncSummary();
        var historyKeyAllocator = new ArchiveHistoryKeyAllocator(
            context.TargetStore,
            context.TargetDescriptor.Layout,
            _timeProvider.GetUtcNow());

        foreach (var projectedObject in projectedObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = ArchiveLayout.NormalizeObjectKey(projectedObject.RelativePath);
            if (targetObjects.TryGetValue(relativePath, out var targetObject))
            {
                targetObjects.Remove(relativePath);
                if (await HasSameContentAsync(
                        projectedObject,
                        context.TargetStore,
                        targetObject,
                        cancellationToken))
                {
                    summary.AddUnchanged();
                    continue;
                }

                summary.AddChanged();
                if (writeChanges)
                {
                    await MoveTargetObjectToHistoryAsync(
                        context.TargetStore,
                        historyKeyAllocator,
                        context.TargetDescriptor.Layout,
                        relativePath,
                        cancellationToken);

                    await UploadProjectedObjectAsync(
                        context.TargetStore,
                        context.TargetDescriptor.Layout,
                        projectedObject,
                        relativePath,
                        cancellationToken);
                }

                continue;
            }

            summary.AddNew();
            if (writeChanges)
            {
                await UploadProjectedObjectAsync(
                    context.TargetStore,
                    context.TargetDescriptor.Layout,
                    projectedObject,
                    relativePath,
                    cancellationToken);
            }
        }

        foreach (var relativePath in targetObjects.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();

            summary.AddExtra();
            if (writeChanges)
            {
                await MoveTargetObjectToHistoryAsync(
                    context.TargetStore,
                    historyKeyAllocator,
                    context.TargetDescriptor.Layout,
                    relativePath,
                    cancellationToken);
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
        var message = BuildSummaryMessage(operationName, summary, verifyOnly);

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

    private static ArchiveProjectionRequest CreateProjectionRequest(ArchiveSyncContext context)
    {
        var sourceStore = new ArchiveFilteredObjectStore
        (
            context.SourceStore,
            CreateInternalObjectKeys(context.SourceDescriptor.Layout),
            CreateInternalObjectPrefixes(context.SourceDescriptor.Layout)
        );

        return new
        (
            sourceStore,
            context.SourcePrefix,
            context.Policy,
            context.SourceRoot
        );
    }

    private static async Task<SortedDictionary<string, ArchiveObjectInfo>> ListTargetLiveObjectsAsync
    (
        IObjectStore targetStore,
        ArchiveLayout targetLayout,
        CancellationToken cancellationToken
    )
    {
        var targetObjects = new SortedDictionary<string, ArchiveObjectInfo>(StringComparer.Ordinal);
        var livePrefix = ArchiveLayout.NormalizeObjectPrefix(targetLayout.LivePrefix);
        var listedTargetObjects = targetStore.ListAsync(
            livePrefix,
            cancellationToken);

        await foreach (var targetObject in listedTargetObjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

            targetObjects.TryAdd(relativePath, targetObject with
            {
                Key = targetKey,
            });
        }

        return targetObjects;
    }

    private static async Task UploadProjectedObjectAsync
    (
        IObjectStore targetStore,
        ArchiveLayout targetLayout,
        ArchiveProjectedObject projectedObject,
        string relativePath,
        CancellationToken cancellationToken
    )
    {
        var targetKey = targetLayout.ToLiveObjectKey(relativePath);
        try
        {
            await using var content = await projectedObject.OpenContentAsync(cancellationToken);

            await targetStore.UploadAsync(
                targetKey,
                content.Content,
                content.ContentType,
                content.Metadata ?? EmptyMetadata,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Sync upload failed for projected object '{relativePath}' to target object '{targetKey}'.",
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

    private static async Task<bool> HasSameContentAsync
    (
        ArchiveProjectedObject projectedObject,
        IObjectStore targetStore,
        ArchiveObjectInfo targetObject,
        CancellationToken cancellationToken
    )
    {
        if (projectedObject.ContentLength.HasValue &&
            targetObject.ContentLength.HasValue &&
            projectedObject.ContentLength.Value != targetObject.ContentLength.Value)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(projectedObject.ContentHash) &&
            !string.IsNullOrWhiteSpace(targetObject.ContentHash))
        {
            return string.Equals(
                projectedObject.ContentHash,
                targetObject.ContentHash,
                StringComparison.Ordinal);
        }

        try
        {
            await using var sourceContent = await projectedObject.OpenContentAsync(cancellationToken);
            await using var targetContent = await targetStore.OpenReadAsync(
                targetObject.Key,
                cancellationToken);

            return await StreamsHaveSameContentAsync(
                sourceContent.Content,
                targetContent.Content,
                DefaultBufferSize,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"Sync content comparison failed for target object '{targetObject.Key}'.",
                ex);
        }
    }

    private static async Task<bool> StreamsHaveSameContentAsync
    (
        Stream source,
        Stream target,
        int bufferSize,
        CancellationToken cancellationToken
    )
    {
        var sourceBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        var targetBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);

        try
        {
            while (true)
            {
                var sourceBytesRead = await source.ReadAsync(
                    sourceBuffer.AsMemory(0, bufferSize),
                    cancellationToken);
                var targetBytesRead = await target.ReadAsync(
                    targetBuffer.AsMemory(0, bufferSize),
                    cancellationToken);

                if (sourceBytesRead != targetBytesRead)
                {
                    return false;
                }

                if (sourceBytesRead == 0)
                {
                    return true;
                }

                var sourceSpan = sourceBuffer.AsSpan(0, sourceBytesRead);
                var targetSpan = targetBuffer.AsSpan(0, targetBytesRead);
                if (!sourceSpan.SequenceEqual(targetSpan))
                {
                    return false;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(sourceBuffer);
            ArrayPool<byte>.Shared.Return(targetBuffer);
        }
    }

    private static IEnumerable<string> CreateInternalObjectKeys(ArchiveLayout layout)
    {
        if (!string.IsNullOrEmpty(ArchiveLayout.NormalizeObjectKey(layout.LivePrefix)))
        {
            return [];
        }

        return [BackupRootFileNames.Primary];
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

        //TODO: Formalize provider-private temporary prefixes instead of hard-coding the filesystem adapter prefix.
        prefixes.Add(".yabt-tmp");
        return prefixes;
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
                    StringComparison.Ordinal))
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
                ArchiveLayout.IsUnderPrefix(objectKey, normalizedPrefix))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildSummaryMessage
    (
        string operationName,
        ArchiveSyncSummary summary,
        bool verifyOnly
    )
    {
        if (verifyOnly &&
            summary.NewCount == 0 &&
            summary.ChangedCount == 0 &&
            summary.ExtraCount == 0)
        {
            return $"Archive {operationName} completed; verified {summary.UnchangedCount} unchanged object(s).";
        }

        return $"Archive {operationName} completed; {summary.NewCount} new object(s), " +
            $"{summary.ChangedCount} changed object(s), {summary.ExtraCount} extra object(s), " +
            $"and {summary.UnchangedCount} unchanged object(s).";
    }

}

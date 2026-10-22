using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class ArchiveOperationContextFactory
(
    ILogger<ArchiveOperationContextFactory> _logger,
    IArchiveLocationResolver _locationResolver,
    IEnumerable<IArchiveFormatHandler> formatHandlers,
    IEnumerable<IBackupRootStoreResolver> storeResolvers
)
{
    private readonly FrozenDictionary<string, IArchiveFormatHandler> _formatHandlers =
        formatHandlers.ToFrozenDictionary
    (
        formatHandler => formatHandler.FormatName,
        StringComparer.Ordinal
    );

    private readonly FrozenDictionary<string, IBackupRootStoreResolver> _storeResolvers = storeResolvers.ToFrozenDictionary
    (
        resolver => resolver.StoreKind,
        StringComparer.Ordinal
    );

    internal async Task<ArchiveSyncContext> CreateContextAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(CreateContextAsync));

        ArgumentNullException.ThrowIfNull(request);
        var sourceLocation = await _locationResolver.LocateSourceAsync
        (
            request.SourceRoot,
            cancellationToken: cancellationToken
        );
        var sourceDescriptor = sourceLocation.Root.Descriptor;
        ArchivePathRules.ValidateInternalLayoutPrefixes(sourceDescriptor.Layout);
        var targetStoreConfiguration = GetTargetStoreConfiguration(sourceDescriptor, request.TargetStoreId);
        if (!_storeResolvers.TryGetValue(targetStoreConfiguration.Kind, out var targetStoreResolver))
        {
            throw new YabtSyncException(
                $"No object store resolver is registered for store kind '{targetStoreConfiguration.Kind}'.");
        }

        var targetStore = targetStoreResolver.ResolveStore
        (
            targetStoreConfiguration,
            sourceLocation.Root.RootPath
        );
        _locationResolver.ValidateBackupLocations(sourceLocation, targetStore);
        _locationResolver.ConfigureSourceTraversal(sourceLocation, targetStore);
        var policy = await _locationResolver.ReadPolicyAsync
        (
            sourceLocation,
            sourceLocation.SelectionPrefix,
            cancellationToken
        );
        if (!_formatHandlers.TryGetValue(policy.Format, out var formatHandler))
        {
            throw new YabtSyncException($"No archive format handler is registered for format '{policy.Format}'.");
        }

        formatHandler.ValidateBackupPolicy(policy, FolderPolicyFileNames.Primary);

        return new
        (
            sourceLocation.DisplayName,
            sourceLocation.SourcePrefix.Value,
            sourceLocation.SourceStore,
            targetStore,
            sourceDescriptor,
            sourceDescriptor,
            sourceLocation.Root.Document,
            policy,
            formatHandler,
            sourceLocation
        );
    }

    internal async Task<RestoreContext> CreateRestoreContextAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(CreateRestoreContextAsync));

        var sourceLocation = await _locationResolver.LocateSourceAsync
        (
            request.SourceRoot,
            requireRoot: true,
            cancellationToken
        );
        ArchivePathRules.ValidateInternalLayoutPrefixes(sourceLocation.Root.Descriptor.Layout);
        var archiveStoreConfiguration = GetTargetStoreConfiguration
        (
            sourceLocation.Root.Descriptor,
            request.TargetStoreId
        );
        if (!_storeResolvers.TryGetValue(archiveStoreConfiguration.Kind, out var archiveStoreResolver))
        {
            throw new YabtSyncException(
                $"No object store resolver is registered for store kind '{archiveStoreConfiguration.Kind}'.");
        }

        return new
        (
            archiveStoreResolver.ResolveStore(archiveStoreConfiguration, sourceLocation.Root.RootPath),
            sourceLocation.Root.Descriptor,
            sourceLocation.Root.Document,
            ArchiveDocument: null,
            RootFormatHandler: null,
            RootIsPackaged: false,
            sourceLocation
        );
    }

    private BackupRootStore GetTargetStoreConfiguration
    (
        BackupRootDescriptor descriptor,
        string? requestedStoreId
    )
    {
        _logger.LogTrace(nameof(GetTargetStoreConfiguration));

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

    internal ArchiveProjectionRequest CreateProjectionRequest(ArchiveSyncContext context)
    {
        _logger.LogTrace(nameof(CreateProjectionRequest));

        var filteredSourceStore = new ArchiveFilteredObjectStore
        (
            context.SourceStore,
            ArchivePathRules.CreateInternalObjectKeys(context.SourceDescriptor.Layout),
            ArchivePathRules.CreateInternalObjectPrefixes(context.SourceDescriptor.Layout)
        );
        var projectionSourceStore = new ArchiveProjectionObjectStore
        (
            filteredSourceStore,
            context.SourceDisplayName,
            context.SourcePrefix,
            (prefix, cancellationToken) => _locationResolver.ReadPolicyAsync
            (
                context.SourceLocation,
                ObjectPath.Parse(prefix),
                cancellationToken
            ),
            _formatHandlers
        );

        return new
        (
            projectionSourceStore,
            context.SourcePrefix,
            context.Policy,
            context.SourceDisplayName
        );
    }
}

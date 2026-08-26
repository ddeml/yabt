using System.Collections.Frozen;
using System.Text;
using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal sealed class ArchiveHistorizer
(
    IArchiveMutableObjectStore _store,
    ArchiveLayout _layout,
    DateTimeOffset historicalTimestamp,
    ILogger _logger
)
{
    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new Dictionary<string, string>(StringComparer.Ordinal)
            .ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly byte[] InvalidationMarkerContent = Encoding.UTF8.GetBytes
    (
        "{\"documentType\":\"yabt.historyManifestInvalidation\",\"schemaVersion\":1}\n"
    );

    private readonly ArchiveHistoryKeyAllocator _historyKeyAllocator = new(
        _store,
        _layout,
        historicalTimestamp);
    private bool _recoveryStateInspected;
    private bool _mutationPrepared;
    private bool _cleanupRequired;

    public async Task InspectRecoveryStateAsync(CancellationToken cancellationToken)
    {
        _logger.LogTrace(nameof(InspectRecoveryStateAsync));

        if (_recoveryStateInspected) { return; }

        var invalidationMarkerKey = _layout.ToHistoryObjectKey(
            ArchiveHistoryManifest.InvalidationMarkerFileName);
        _logger.LogControlMetadataOperation(
            "Checking",
            invalidationMarkerKey);
        _cleanupRequired = await _store.ExistsAsync(
            invalidationMarkerKey,
            cancellationToken);
        _recoveryStateInspected = true;
    }

    public async Task MoveObjectAsync
    (
        string relativePath,
        string operationName,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(MoveObjectAsync));

        await PrepareMutationAsync(cancellationToken);
        var sourceKey = _layout.ToLiveObjectKey(relativePath);
        var destinationKey = await _historyKeyAllocator.CreateHistoricalKeyAsync(
            relativePath,
            cancellationToken);
        try
        {
            await _store.MoveAsync(sourceKey, destinationKey, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"{operationName} history move failed for object '{sourceKey}' to " +
                    $"'{destinationKey}'.",
                ex);
        }
    }

    public async Task MoveRootObjectAsync
    (
        string rootRelativePath,
        string operationName,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(MoveRootObjectAsync));

        await PrepareMutationAsync(cancellationToken);
        var sourceKey = ArchiveLayout.NormalizeObjectKey(rootRelativePath);
        var destinationKey = await _historyKeyAllocator.CreateHistoricalKeyAsync(
            rootRelativePath,
            cancellationToken);
        try
        {
            await _store.MoveAsync(sourceKey, destinationKey, cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"{operationName} history move failed for root object '{sourceKey}' to " +
                    $"'{destinationKey}'.",
                ex);
        }
    }

    public async Task MoveFolderAsync
    (
        string relativePath,
        string operationName,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(MoveFolderAsync));

        await PrepareMutationAsync(cancellationToken);
        var sourcePrefix = _layout.ToLiveObjectKey(relativePath);
        var destinationPrefix = await _historyKeyAllocator.CreateHistoricalKeyAsync(
            relativePath,
            cancellationToken);
        try
        {
            await _store.MoveFolderAsync(
                sourcePrefix,
                destinationPrefix,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"{operationName} history move failed for folder '{sourcePrefix}' to " +
                    $"'{destinationPrefix}'.",
                ex);
        }
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        _logger.LogTrace(nameof(CompleteAsync));

        await InspectRecoveryStateAsync(cancellationToken);
        if (!_cleanupRequired) { return; }

        var manifestKey = _layout.ToHistoryObjectKey(ArchiveHistoryFileNames.Manifest);
        var markerKey = _layout.ToHistoryObjectKey(
            ArchiveHistoryManifest.InvalidationMarkerFileName);
        await DeleteInternalObjectAsync(
            manifestKey,
            "Stale history manifest",
            cancellationToken);
        await DeleteInternalObjectAsync(
            markerKey,
            "History manifest invalidation marker",
            cancellationToken);
        _cleanupRequired = false;
    }

    private async Task PrepareMutationAsync(CancellationToken cancellationToken)
    {
        _logger.LogTrace(nameof(PrepareMutationAsync));

        if (_mutationPrepared) { return; }

        await InspectRecoveryStateAsync(cancellationToken);
        var manifestKey = _layout.ToHistoryObjectKey(ArchiveHistoryFileNames.Manifest);
        var markerKey = _layout.ToHistoryObjectKey(
            ArchiveHistoryManifest.InvalidationMarkerFileName);
        var manifestExists = await _store.ExistsAsync(
            manifestKey,
            cancellationToken);
        _logger.LogControlMetadataOperation("Checked", manifestKey);
        var markerExists = await _store.ExistsAsync(
            markerKey,
            cancellationToken);
        _logger.LogControlMetadataOperation("Checked", markerKey);
        if (manifestExists && !markerExists)
        {
            await using var markerContent = new MemoryStream(
                InvalidationMarkerContent,
                writable: false);
            await _store.UploadAsync(
                markerKey,
                markerContent,
                "application/json",
                EmptyMetadata,
                cancellationToken);
            _logger.LogControlMetadataOperation("Wrote", markerKey);
            markerExists = true;
        }

        _cleanupRequired = manifestExists || markerExists;
        _mutationPrepared = true;
    }

    private async Task DeleteInternalObjectAsync
    (
        string key,
        string description,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(DeleteInternalObjectAsync));

        try
        {
            if (!await _store.ExistsAsync(key, cancellationToken)) { return; }

            _logger.LogControlMetadataOperation(
                "Reading for guarded deletion",
                key);
            var expectedContentHash = await ComputeStoredObjectHashAsync(
                key,
                cancellationToken);
            var deleted = await _store.TryDeleteIfContentHashMatchesAsync(
                key,
                expectedContentHash,
                cancellationToken);
            if (!deleted)
            {
                throw new YabtSyncException(
                    $"{description} changed before it could be deleted.");
            }

            _logger.LogControlMetadataOperation("Deleted", key);
        }
        catch (Exception ex)
        {
            throw new YabtSyncException(
                $"{description} at '{key}' could not be deleted.",
                ex);
        }
    }

    private async Task<string> ComputeStoredObjectHashAsync
    (
        string key,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(ComputeStoredObjectHashAsync));

        await using var content = await _store.OpenReadAsync(key, cancellationToken);
        using var hashingContent = new ContentHashingReadStream(content.Content);
        await hashingContent.CopyToAsync(Stream.Null, cancellationToken);
        return hashingContent.CompleteHash();
    }
}

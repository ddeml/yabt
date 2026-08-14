using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

internal sealed class ArchiveFilteredObjectStore
(
    IReadOnlyObjectStore _inner,
    IEnumerable<string>? excludedObjectKeys = null,
    IEnumerable<string>? excludedObjectPrefixes = null
) : IReadOnlyObjectStore
{
    private readonly FrozenSet<string> _excludedObjectKeys = NormalizeObjectKeys(excludedObjectKeys);
    private readonly FrozenSet<string> _excludedObjectPrefixes = NormalizeObjectPrefixes(excludedObjectPrefixes);

    public Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        return _inner.EnsureReadyAsync(cancellationToken);
    }

    public Task<ArchiveObjectContent> OpenReadAsync
    (
        string key,
        CancellationToken cancellationToken = default
    )
    {
        return _inner.OpenReadAsync(
            NormalizeAllowedObjectKey(key),
            cancellationToken);
    }

    public Task<bool> ExistsAsync
    (
        string key,
        CancellationToken cancellationToken = default
    )
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedKey = ArchiveLayout.NormalizeObjectKey(key);
        return IsExcluded(normalizedKey) ?
            Task.FromResult(false) :
            _inner.ExistsAsync(normalizedKey, cancellationToken);
    }

    public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
    (
        string? folderPrefix,
        bool recursive = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var folderItems = _inner.GetFolderItemsAsync(
            folderPrefix,
            recursive,
            cancellationToken);
        await foreach (var folderItem in folderItems)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedKey = ArchiveLayout.NormalizeObjectKey(folderItem.Key);
            if (IsExcluded(normalizedKey))
            {
                continue;
            }

            yield return folderItem with
            {
                Key = normalizedKey,
                Object = folderItem.Object is null ? null : folderItem.Object with
                {
                    Key = normalizedKey,
                },
            };
        }
    }

    private string NormalizeAllowedObjectKey(string key)
    {
        var normalizedKey = ArchiveLayout.NormalizeObjectKey(key);
        if (IsExcluded(normalizedKey))
        {
            throw new YabtSyncException($"Object '{normalizedKey}' is excluded from the filtered object store.");
        }

        return normalizedKey;
    }

    private bool IsExcluded(string objectKey)
    {
        return _excludedObjectKeys.Contains(objectKey) ||
            _excludedObjectPrefixes.Any(prefix => ArchiveLayout.IsUnderPrefix(objectKey, prefix));
    }

    private static FrozenSet<string> NormalizeObjectKeys(IEnumerable<string>? keys)
    {
        if (keys is null)
        {
            return FrozenSet<string>.Empty;
        }

        return keys
            .Select(ArchiveLayout.NormalizeObjectKey)
            .Where(key => !string.IsNullOrEmpty(key))
            .ToFrozenSet(StringComparer.Ordinal);
    }

    private static FrozenSet<string> NormalizeObjectPrefixes(IEnumerable<string>? prefixes)
    {
        if (prefixes is null)
        {
            return FrozenSet<string>.Empty;
        }

        return prefixes
            .Select(ArchiveLayout.NormalizeObjectPrefix)
            .Where(prefix => !string.IsNullOrEmpty(prefix))
            .Select(prefix => prefix!)
            .ToFrozenSet(StringComparer.Ordinal);
    }
}

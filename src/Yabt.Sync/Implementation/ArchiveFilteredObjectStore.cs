using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

internal sealed class ArchiveFilteredObjectStore
(
    IObjectStore _inner,
    IEnumerable<string>? excludedObjectKeys = null,
    IEnumerable<string>? excludedObjectPrefixes = null
) : IObjectStore
{
    private readonly FrozenSet<string> _excludedObjectKeys = NormalizeObjectKeys(excludedObjectKeys);
    private readonly FrozenSet<string> _excludedObjectPrefixes = NormalizeObjectPrefixes(excludedObjectPrefixes);

    public Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        return _inner.EnsureReadyAsync(cancellationToken);
    }

    public Task UploadAsync
    (
        string key,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default
    )
    {
        return _inner.UploadAsync(
            NormalizeAllowedObjectKey(key),
            content,
            contentType,
            metadata,
            cancellationToken);
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
        return _inner.ExistsAsync(
            NormalizeAllowedObjectKey(key),
            cancellationToken);
    }

    public async IAsyncEnumerable<ArchiveObjectInfo> ListAsync
    (
        string? prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var objects = _inner.ListAsync(prefix, cancellationToken);
        await foreach (var archiveObject in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var normalizedKey = ArchiveLayout.NormalizeObjectKey(archiveObject.Key);
            if (IsExcluded(normalizedKey))
            {
                continue;
            }

            yield return archiveObject with
            {
                Key = normalizedKey,
            };
        }
    }

    public Task MoveAsync
    (
        string source,
        string destination,
        CancellationToken cancellationToken = default
    )
    {
        return _inner.MoveAsync(
            NormalizeAllowedObjectKey(source),
            NormalizeAllowedObjectKey(destination),
            cancellationToken);
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

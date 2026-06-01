using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yabt.Common;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Tests;

public sealed class MemoryObjectStore
(
    TimeProvider timeProvider,
    ILogger<MemoryObjectStore> logger,
    bool _provideContentHash = default
) : IObjectStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, StoredInMemoryArchiveObject> _objects = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider = Check.NotNull(timeProvider);
    private readonly ILogger<MemoryObjectStore> _logger = Check.NotNull(logger);

    public MemoryObjectStore()
        : this(TimeProvider.System)
    {
    }

    public MemoryObjectStore(bool provideContentHash)
        : this(TimeProvider.System, provideContentHash)
    {
    }

    public MemoryObjectStore(TimeProvider timeProvider)
        : this(timeProvider, NullLogger<MemoryObjectStore>.Instance)
    {
    }

    public MemoryObjectStore
    (
        TimeProvider timeProvider,
        bool provideContentHash
    )
        : this
        (
            timeProvider,
            NullLogger<MemoryObjectStore>.Instance,
            provideContentHash
        )
    {
    }

    public Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogTrace(nameof(EnsureReadyAsync));

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task UploadAsync
    (
        string key,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(UploadAsync));

        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(metadata);

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedKey = NormalizeObjectKey(key);
        using var memory = new MemoryStream();
        await content.CopyToAsync(memory, cancellationToken);

        var storedObject = new StoredInMemoryArchiveObject(
            memory.ToArray(),
            contentType,
            CopyMetadata(metadata),
            _timeProvider.GetUtcNow());

        lock (_gate)
        {
            if (_objects.ContainsKey(normalizedKey))
            {
                throw new YabtTestsException(
                    $"In-memory object '{normalizedKey}' already exists.",
                    normalizedKey);
            }

            _objects.Add(normalizedKey, storedObject);
        }
    }

    public Task<ArchiveObjectContent> OpenReadAsync
    (
        string key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(OpenReadAsync));

        cancellationToken.ThrowIfCancellationRequested();

        var archiveObject = GetObject(key);
        return Task.FromResult(new ArchiveObjectContent(
            new MemoryStream(archiveObject.Content.ToArray(), writable: false),
            archiveObject.ContentType,
            archiveObject.Metadata));
    }

    public Task<bool> ExistsAsync
    (
        string key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ExistsAsync));

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedKey = NormalizeObjectKey(key);
        lock (_gate)
        {
            return Task.FromResult(_objects.ContainsKey(normalizedKey));
        }
    }

    public async IAsyncEnumerable<ArchiveObjectInfo> ListAsync
    (
        string? prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ListAsync));

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPrefix = NormalizeObjectPrefix(prefix);
        IReadOnlyList<InMemoryArchiveObject> objects;

        lock (_gate)
        {
            objects = _objects
                .Where(candidate => IsUnderPrefix(candidate.Key, normalizedPrefix))
                .OrderBy(candidate => candidate.Key, StringComparer.Ordinal)
                .Select(candidate => ToPublicObject(candidate.Key, candidate.Value))
                .ToList();
        }

        await Task.Yield();

        foreach (var archiveObject in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contentHash = _provideContentHash ?
                ComputeContentHash(archiveObject.Content) :
                null;

            yield return new
            (
                archiveObject.Key,
                archiveObject.Content.Length,
                archiveObject.LastModifiedUtc,
                contentHash
            );
        }
    }

    public Task MoveAsync
    (
        string source,
        string destination,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(MoveAsync));

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSource = NormalizeObjectKey(source);
        var normalizedDestination = NormalizeObjectKey(destination);

        lock (_gate)
        {
            if (!_objects.Remove(normalizedSource, out var storedObject))
            {
                throw new YabtTestsException(
                    $"In-memory source object '{normalizedSource}' does not exist.",
                    normalizedSource);
            }

            if (_objects.ContainsKey(normalizedDestination))
            {
                _objects.Add(normalizedSource, storedObject);
                throw new YabtTestsException(
                    $"In-memory destination object '{normalizedDestination}' already exists.",
                    normalizedDestination);
            }

            _objects.Add(normalizedDestination, storedObject);
        }

        return Task.CompletedTask;
    }

    public bool TryGetObject
    (
        string key,
        [NotNullWhen(true)] out InMemoryArchiveObject? archiveObject
    )
    {
        _logger.LogTrace(nameof(TryGetObject));

        var normalizedKey = NormalizeObjectKey(key);
        lock (_gate)
        {
            if (_objects.TryGetValue(normalizedKey, out var storedObject))
            {
                archiveObject = ToPublicObject(normalizedKey, storedObject);
                return true;
            }
        }

        archiveObject = null;
        return false;
    }

    public InMemoryArchiveObject GetObject(string key)
    {
        _logger.LogTrace(nameof(GetObject));

        if (TryGetObject(key, out var archiveObject))
        {
            return archiveObject;
        }

        var normalizedKey = NormalizeObjectKey(key);
        throw new YabtTestsException(
            $"In-memory object '{normalizedKey}' does not exist.",
            normalizedKey);
    }

    public IReadOnlyList<InMemoryArchiveObject> Snapshot()
    {
        _logger.LogTrace(nameof(Snapshot));

        lock (_gate)
        {
            return _objects
                .OrderBy(candidate => candidate.Key, StringComparer.Ordinal)
                .Select(candidate => ToPublicObject(candidate.Key, candidate.Value))
                .ToArray();
        }
    }

    public void Clear()
    {
        _logger.LogTrace(nameof(Clear));

        lock (_gate)
        {
            _objects.Clear();
        }
    }

    private static string NormalizeObjectKey(string? value)
    {
        var normalized = NormalizeObjectPrefix(value);
        if (string.IsNullOrEmpty(normalized))
        {
            throw new YabtTestsException("In-memory object key must not be empty.");
        }

        return normalized;
    }

    private static string NormalizeObjectPrefix(string? value)
    {
        try
        {
            return ArchiveLayout.NormalizeObjectKey(value);
        }
        catch (Exception ex)
        {
            throw new YabtTestsException("In-memory object path contains an invalid segment.", ex);
        }
    }

    private static bool IsUnderPrefix(string relativePath, string prefix)
    {
        return string.IsNullOrEmpty(prefix) ||
            string.Equals(relativePath, prefix, StringComparison.Ordinal) ||
            relativePath.StartsWith($"{prefix}/", StringComparison.Ordinal);
    }

    private static ReadOnlyDictionary<string, string> CopyMetadata
    (
        IReadOnlyDictionary<string, string> metadata
    )
    {
        return new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(metadata, StringComparer.Ordinal));
    }

    private static string ComputeContentHash(ReadOnlyMemory<byte> content)
    {
        var hash = new XxHash128();
        hash.Append(content.Span);

        return $"xxh128:{Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()}";
    }

    private static InMemoryArchiveObject ToPublicObject
    (
        string key,
        StoredInMemoryArchiveObject storedObject
    )
    {
        return new
        (
            key,
            storedObject.Content.ToArray(),
            storedObject.ContentType,
            CopyMetadata(storedObject.Metadata),
            storedObject.LastModifiedUtc
        );
    }

    private sealed record StoredInMemoryArchiveObject
    (
        byte[] Content,
        string ContentType,
        IReadOnlyDictionary<string, string> Metadata,
        DateTimeOffset LastModifiedUtc
    );
}

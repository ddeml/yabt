using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Tests;

public sealed class InMemoryObjectStore : IObjectStore
{
    private readonly object _gate = new();
    private readonly Dictionary<ArchiveObjectKey, StoredInMemoryArchiveObject> _objects = [];
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InMemoryObjectStore> _logger;

    public InMemoryObjectStore()
        : this(TimeProvider.System)
    {
    }

    public InMemoryObjectStore(TimeProvider timeProvider)
        : this(timeProvider, NullLogger<InMemoryObjectStore>.Instance)
    {
    }

    public InMemoryObjectStore
    (
        TimeProvider timeProvider,
        ILogger<InMemoryObjectStore> logger
    )
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogTrace(nameof(EnsureReadyAsync));

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task UploadAsync
    (
        ArchiveObjectKey key,
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

        var normalizedKey = NormalizeKey(key);
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
                    $"In-memory object '{normalizedKey.ToObjectPath()}' already exists.",
                    normalizedKey);
            }

            _objects.Add(normalizedKey, storedObject);
        }
    }

    public Task<ArchiveObjectContent> OpenReadAsync
    (
        ArchiveObjectKey key,
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
        ArchiveObjectKey key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ExistsAsync));

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedKey = NormalizeKey(key);
        lock (_gate)
        {
            return Task.FromResult(_objects.ContainsKey(normalizedKey));
        }
    }

    public async IAsyncEnumerable<ArchiveObjectInfo> ListAsync
    (
        ArchiveArea area,
        string? prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ListAsync));

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedArea = NormalizeArea(area);
        var normalizedPrefix = NormalizeRelativePath(prefix);
        IReadOnlyList<InMemoryArchiveObject> objects;

        lock (_gate)
        {
            objects = _objects
                .Where(candidate =>
                    candidate.Key.Area == normalizedArea &&
                    IsUnderPrefix(candidate.Key.RelativePath, normalizedPrefix))
                .OrderBy(candidate => candidate.Key.RelativePath, StringComparer.Ordinal)
                .Select(candidate => ToPublicObject(candidate.Key, candidate.Value))
                .ToArray();
        }

        await Task.Yield();

        foreach (var archiveObject in objects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return new(
                archiveObject.Key,
                archiveObject.Content.Length,
                archiveObject.LastModifiedUtc);
        }
    }

    public Task MoveAsync
    (
        ArchiveObjectKey source,
        ArchiveObjectKey destination,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(MoveAsync));

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSource = NormalizeKey(source);
        var normalizedDestination = NormalizeKey(destination);

        lock (_gate)
        {
            if (!_objects.Remove(normalizedSource, out var storedObject))
            {
                throw new YabtTestsException(
                    $"In-memory source object '{normalizedSource.ToObjectPath()}' does not exist.",
                    normalizedSource);
            }

            if (_objects.ContainsKey(normalizedDestination))
            {
                _objects.Add(normalizedSource, storedObject);
                throw new YabtTestsException(
                    $"In-memory destination object '{normalizedDestination.ToObjectPath()}' already exists.",
                    normalizedDestination);
            }

            _objects.Add(normalizedDestination, storedObject);
        }

        return Task.CompletedTask;
    }

    public bool TryGetObject
    (
        ArchiveObjectKey key,
        [NotNullWhen(true)] out InMemoryArchiveObject? archiveObject
    )
    {
        _logger.LogTrace(nameof(TryGetObject));

        var normalizedKey = NormalizeKey(key);
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

    public InMemoryArchiveObject GetObject(ArchiveObjectKey key)
    {
        _logger.LogTrace(nameof(GetObject));

        if (TryGetObject(key, out var archiveObject))
        {
            return archiveObject;
        }

        var normalizedKey = NormalizeKey(key);
        throw new YabtTestsException(
            $"In-memory object '{normalizedKey.ToObjectPath()}' does not exist.",
            normalizedKey);
    }

    public IReadOnlyList<InMemoryArchiveObject> Snapshot()
    {
        _logger.LogTrace(nameof(Snapshot));

        lock (_gate)
        {
            return _objects
                .OrderBy(candidate => candidate.Key.Area)
                .ThenBy(candidate => candidate.Key.RelativePath, StringComparer.Ordinal)
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

    private static ArchiveObjectKey NormalizeKey(ArchiveObjectKey key)
    {
        return new(
            NormalizeArea(key.Area),
            NormalizeRelativePath(key.RelativePath));
    }

    private static ArchiveArea NormalizeArea(ArchiveArea area)
    {
        return area switch
        {
            ArchiveArea.Live => ArchiveArea.Live,
            ArchiveArea.Hist => ArchiveArea.Hist,
            _ => throw new ArgumentOutOfRangeException(nameof(area), area, null),
        };
    }

    private static string NormalizeRelativePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var segments = value
            .Replace('\\', '/')
            .Trim('/')
            .Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            if (segment is "." or "..")
            {
                throw new YabtTestsException("In-memory object path contains an invalid segment.");
            }
        }

        return string.Join('/', segments);
    }

    private static bool IsUnderPrefix(string relativePath, string prefix)
    {
        return string.IsNullOrEmpty(prefix) ||
            relativePath.StartsWith($"{prefix}/", StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> CopyMetadata
    (
        IReadOnlyDictionary<string, string> metadata
    )
    {
        return new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(metadata, StringComparer.Ordinal));
    }

    private static InMemoryArchiveObject ToPublicObject
    (
        ArchiveObjectKey key,
        StoredInMemoryArchiveObject storedObject
    )
    {
        return new(
            key,
            storedObject.Content.ToArray(),
            storedObject.ContentType,
            CopyMetadata(storedObject.Metadata),
            storedObject.LastModifiedUtc);
    }

    private sealed record StoredInMemoryArchiveObject
    (
        byte[] Content,
        string ContentType,
        IReadOnlyDictionary<string, string> Metadata,
        DateTimeOffset LastModifiedUtc
    );
}

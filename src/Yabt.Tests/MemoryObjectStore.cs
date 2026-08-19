using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
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
) : IArchiveMutableObjectStore
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

    public async Task<bool> TryReplaceIfContentHashMatchesAsync
    (
        string key,
        string expectedContentHash,
        Stream replacementContent,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(TryReplaceIfContentHashMatchesAsync));

        ArgumentNullException.ThrowIfNull(replacementContent);
        ArgumentNullException.ThrowIfNull(metadata);
        ValidateExpectedContentHash(expectedContentHash);

        var normalizedKey = NormalizeObjectKey(key);
        using var memory = new MemoryStream();
        await replacementContent.CopyToAsync(memory, cancellationToken);
        var replacement = new StoredInMemoryArchiveObject
        (
            memory.ToArray(),
            contentType,
            CopyMetadata(metadata),
            _timeProvider.GetUtcNow()
        );

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_objects.TryGetValue(normalizedKey, out var current) ||
                !string.Equals(
                    ComputeContentHash(current.Content),
                    expectedContentHash,
                    StringComparison.Ordinal))
            {
                return false;
            }

            _objects[normalizedKey] = replacement;
            return true;
        }
    }

    public Task<bool> TryDeleteIfContentHashMatchesAsync
    (
        string key,
        string expectedContentHash,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(TryDeleteIfContentHashMatchesAsync));

        cancellationToken.ThrowIfCancellationRequested();
        ValidateExpectedContentHash(expectedContentHash);

        var normalizedKey = NormalizeObjectKey(key);
        lock (_gate)
        {
            if (!_objects.TryGetValue(normalizedKey, out var current) ||
                !string.Equals(
                    ComputeContentHash(current.Content),
                    expectedContentHash,
                    StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            _objects.Remove(normalizedKey);
            return Task.FromResult(true);
        }
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

    public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
    (
        string? folderPrefix,
        bool recursive = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(GetFolderItemsAsync));

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedPrefix = NormalizeObjectPrefix(folderPrefix);
        IReadOnlyList<ArchiveFolderItem> items;

        lock (_gate)
        {
            var folderItems = new Dictionary<(string Key, bool IsFolder), ArchiveFolderItem>();
            foreach (var candidate in _objects.OrderBy(candidate => candidate.Key, StringComparer.Ordinal))
            {
                if (!TryCreateFolderItem(
                        candidate.Key,
                        candidate.Value,
                        normalizedPrefix,
                        recursive,
                        out var item))
                {
                    continue;
                }

                folderItems.TryAdd((item.Key, item.IsFolder), item);
            }

            items = folderItems.Values.ToArray();
        }

        await Task.Yield();

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
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

    public Task MoveFolderAsync
    (
        string sourcePrefix,
        string destinationPrefix,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(MoveFolderAsync));

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedSourcePrefix = NormalizeObjectKey(sourcePrefix);
        var normalizedDestinationPrefix = NormalizeObjectKey(destinationPrefix);
        if (ArchiveLayout.IsUnderPrefix(normalizedDestinationPrefix, normalizedSourcePrefix))
        {
            throw new YabtTestsException(
                "In-memory folder destination must not be the source folder or one of its descendants.",
                normalizedDestinationPrefix);
        }

        var sourceObjectPrefix = $"{normalizedSourcePrefix}/";

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var moves = _objects
                .Where(candidate => candidate.Key.StartsWith(sourceObjectPrefix, StringComparison.Ordinal))
                .Select(candidate => new
                {
                    Source = candidate.Key,
                    Destination = ArchiveLayout.CombinePrefixAndRelativePath
                    (
                        normalizedDestinationPrefix,
                        candidate.Key[sourceObjectPrefix.Length..]
                    ),
                    candidate.Value,
                })
                .ToList();

            if (moves.Count == 0)
            {
                throw new YabtTestsException(
                    $"In-memory source folder '{normalizedSourcePrefix}' does not exist.",
                    normalizedSourcePrefix);
            }

            var destinationObjectPrefix = $"{normalizedDestinationPrefix}/";
            var destinationCollision = _objects.Keys.FirstOrDefault(
                key => string.Equals(key, normalizedDestinationPrefix, StringComparison.Ordinal) ||
                    key.StartsWith(destinationObjectPrefix, StringComparison.Ordinal));
            if (destinationCollision is not null)
            {
                throw new YabtTestsException(
                    $"In-memory destination path '{destinationCollision}' already exists.",
                    destinationCollision);
            }

            foreach (var move in moves)
            {
                _objects.Remove(move.Source);
                _objects.Add(move.Destination, move.Value);
            }
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

    private bool TryCreateFolderItem
    (
        string objectKey,
        StoredInMemoryArchiveObject storedObject,
        string folderPrefix,
        bool recursive,
        [NotNullWhen(true)] out ArchiveFolderItem? item
    )
    {
        if (!IsUnderPrefix(objectKey, folderPrefix))
        {
            item = null;
            return false;
        }

        var relativePath = ArchiveLayout.RemovePrefix(objectKey, folderPrefix);
        if (string.IsNullOrEmpty(relativePath))
        {
            item = null;
            return false;
        }

        var separator = relativePath.IndexOf('/');
        if (separator >= 0)
        {
            if (recursive)
            {
                var objectName = GetObjectName(objectKey);
                if (string.Equals(
                        objectName,
                        ArchiveFolderMarkerFileNames.EmptyFolder,
                        StringComparison.Ordinal))
                {
                    item = null;
                    return false;
                }

                item = ArchiveFolderItem.CreateObject(
                    objectName,
                    ToArchiveObjectInfo(objectKey, storedObject));
                return true;
            }

            var folderName = relativePath[..separator];
            item = ArchiveFolderItem.CreateFolder(
                folderName,
                ArchiveLayout.CombinePrefixAndRelativePath(folderPrefix, folderName));
            return true;
        }

        if (string.Equals(
                relativePath,
                ArchiveFolderMarkerFileNames.EmptyFolder,
                StringComparison.Ordinal))
        {
            item = null;
            return false;
        }

        var archiveObject = ToArchiveObjectInfo(objectKey, storedObject);
        item = ArchiveFolderItem.CreateObject(
            relativePath,
            archiveObject);
        return true;
    }

    private static string GetObjectName(string key)
    {
        var normalizedKey = NormalizeObjectKey(key);
        var separator = normalizedKey.LastIndexOf('/');

        return separator < 0 ? normalizedKey : normalizedKey[(separator + 1)..];
    }

    private ArchiveObjectInfo ToArchiveObjectInfo
    (
        string key,
        StoredInMemoryArchiveObject storedObject
    )
    {
        var contentHash = _provideContentHash ?
            ComputeContentHash(storedObject.Content) :
            null;

        return new
        (
            key,
            storedObject.Content.Length,
            storedObject.LastModifiedUtc,
            contentHash
        );
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
        => ArchiveHash.Compute(content.Span);

    private static void ValidateExpectedContentHash(string expectedContentHash)
    {
        if (!ArchiveHash.IsValid(expectedContentHash))
        {
            throw new ArgumentException
            (
                "The expected content hash must be a canonical YABT xxHash128 hash.",
                nameof(expectedContentHash)
            );
        }
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

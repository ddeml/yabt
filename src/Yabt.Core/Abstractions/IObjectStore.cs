using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

public interface IObjectStore
{
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);

    Task UploadAsync
    (
        ArchiveObjectKey key,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default
    );

    Task<ArchiveObjectContent> OpenReadAsync
    (
        ArchiveObjectKey key,
        CancellationToken cancellationToken = default
    );

    Task<bool> ExistsAsync(ArchiveObjectKey key, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ArchiveObjectInfo> ListAsync
    (
        ArchiveArea area,
        string? prefix,
        CancellationToken cancellationToken = default
    );

    Task MoveAsync
    (
        ArchiveObjectKey source,
        ArchiveObjectKey destination,
        CancellationToken cancellationToken = default
    );
}

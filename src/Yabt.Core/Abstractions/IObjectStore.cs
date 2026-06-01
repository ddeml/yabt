using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

public interface IObjectStore
{
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);

    Task UploadAsync
    (
        string key,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default
    );

    Task<ArchiveObjectContent> OpenReadAsync
    (
        string key,
        CancellationToken cancellationToken = default
    );

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ArchiveObjectInfo> ListAsync
    (
        string? prefix,
        CancellationToken cancellationToken = default
    );

    Task MoveAsync
    (
        string source,
        string destination,
        CancellationToken cancellationToken = default
    );
}

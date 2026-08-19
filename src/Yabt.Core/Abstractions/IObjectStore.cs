using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

public interface IObjectStore : IReadOnlyObjectStore
{
    /// <summary>
    /// Creates one complete object at <paramref name="key"/>. Implementations must fail without
    /// overwriting when that key already exists, and must not expose a partially uploaded final
    /// object. YABT uses this create-if-absent contract for immutable archive objects and locks.
    /// </summary>
    Task UploadAsync
    (
        string key,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default
    );

    Task MoveAsync
    (
        string source,
        string destination,
        CancellationToken cancellationToken = default
    );

    Task MoveFolderAsync
    (
        string sourcePrefix,
        string destinationPrefix,
        CancellationToken cancellationToken = default
    );
}

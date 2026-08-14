using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

public interface IObjectStore : IReadOnlyObjectStore
{
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

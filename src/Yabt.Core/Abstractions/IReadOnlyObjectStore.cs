using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

public interface IReadOnlyObjectStore
{
    /// <summary>
    /// Canonical destination naming rules. Providers with nonordinal semantics override this;
    /// wrappers preserve the wrapped store's namespace capability.
    /// </summary>
    IObjectStoreNamespace Namespace => ObjectStoreNamespace.Ordinal;

    /// <summary>
    /// Validates access to the existing store without creating or changing any resources.
    /// An unavailable store is an error, not an empty source. Subsequent reads and listings
    /// must still report failures because readiness does not provide a snapshot.
    /// </summary>
    Task EnsureReadyAsync(CancellationToken cancellationToken = default);

    Task<ArchiveObjectContent> OpenReadAsync
    (
        string key,
        CancellationToken cancellationToken = default
    );

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
    (
        string? folderPrefix,
        bool recursive = false,
        CancellationToken cancellationToken = default
    );
}

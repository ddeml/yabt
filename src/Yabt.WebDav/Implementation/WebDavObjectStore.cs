using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.WebDav.Implementation;

internal sealed class WebDavObjectStore(ILogger<WebDavObjectStore> _logger) : IObjectStore
{
    public Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogTrace(nameof(EnsureReadyAsync));

        throw new NotImplementedException("WebDAV object store is scaffolded but not implemented yet.");
    }

    public Task UploadAsync
    (
        ArchiveObjectKey key,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(UploadAsync));

        throw new NotImplementedException("WebDAV object store is scaffolded but not implemented yet.");
    }

    public Task<bool> ExistsAsync
    (
        ArchiveObjectKey key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ExistsAsync));

        throw new NotImplementedException("WebDAV object store is scaffolded but not implemented yet.");
    }

    public IAsyncEnumerable<ArchiveObjectInfo> ListAsync
    (
        ArchiveArea area,
        string? prefix,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ListAsync));

        throw new NotImplementedException("WebDAV object store is scaffolded but not implemented yet.");
    }

    public Task MoveAsync
    (
        ArchiveObjectKey source,
        ArchiveObjectKey destination,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(MoveAsync));

        throw new NotImplementedException("WebDAV object store is scaffolded but not implemented yet.");
    }
}

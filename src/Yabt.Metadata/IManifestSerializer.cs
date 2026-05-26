using Yabt.Core.Models;

namespace Yabt.Metadata;

public interface IManifestSerializer
{
    Task WriteAsync
    (
        ArchiveManifest manifest,
        Stream destination,
        CancellationToken cancellationToken = default
    );

    Task<ArchiveManifest> ReadAsync
    (
        Stream source,
        CancellationToken cancellationToken = default
    );
}

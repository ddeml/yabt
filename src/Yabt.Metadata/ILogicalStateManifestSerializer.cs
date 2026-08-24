namespace Yabt.Metadata;

public interface ILogicalStateManifestSerializer
{
    ArchiveLogicalStateManifest Create
    (
        IEnumerable<ArchiveLogicalStateManifestEntry> entries
    );

    Task WriteAsync
    (
        ArchiveLogicalStateManifest manifest,
        Stream destination,
        CancellationToken cancellationToken = default
    );

    Task<ArchiveLogicalStateManifest> ReadAsync
    (
        Stream source,
        CancellationToken cancellationToken = default
    );
}

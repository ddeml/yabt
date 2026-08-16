namespace Yabt.Metadata;

public interface IChangeManifestSerializer
{
    ArchiveChangeManifest Create(IEnumerable<ArchiveChangeManifestEntry> entries);

    Task WriteAsync
    (
        ArchiveChangeManifest manifest,
        Stream destination,
        CancellationToken cancellationToken = default
    );

    Task<ArchiveChangeManifest> ReadAsync
    (
        Stream source,
        CancellationToken cancellationToken = default
    );
}

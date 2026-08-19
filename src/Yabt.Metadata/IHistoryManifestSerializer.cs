namespace Yabt.Metadata;

public interface IHistoryManifestSerializer
{
    ArchiveHistoryManifest Create(IEnumerable<ArchiveHistoryManifestEntry> entries);

    Task WriteAsync
    (
        ArchiveHistoryManifest manifest,
        Stream destination,
        CancellationToken cancellationToken = default
    );

    Task<ArchiveHistoryManifest> ReadAsync
    (
        Stream source,
        CancellationToken cancellationToken = default
    );
}

namespace Yabt.Metadata;

public interface IHistoryReferenceSerializer
{
    ArchiveHistoryContentReference Create(ArchiveHistoryManifestEntry entry);

    Task WriteAsync
    (
        ArchiveHistoryContentReference reference,
        Stream destination,
        CancellationToken cancellationToken = default
    );

    Task<ArchiveHistoryContentReference> ReadAsync
    (
        Stream source,
        CancellationToken cancellationToken = default
    );
}

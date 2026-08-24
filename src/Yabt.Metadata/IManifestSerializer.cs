using Yabt.Core.Models;

namespace Yabt.Metadata;

public interface IManifestSerializer
{
    ArchiveManifest Create
    (
        string sourcePath,
        DateTimeOffset createdAtUtc,
        string format,
        int formatVersion,
        string projectionId,
        string packageName,
        FolderPolicy policy,
        IEnumerable<ArchiveManifestEntry> entries
    );

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

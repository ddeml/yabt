using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

public interface IArchiveFormatHandler
{
    string FormatName { get; }

    bool ProjectsBesideSourceFolder { get; }

    bool CanRestoreArtifact(ArchiveProjectedObject artifact);

    IAsyncEnumerable<ArchiveProjectedObject> ProjectBackupAsync
    (
        ArchiveProjectionRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ArchiveRestoreProjection> ProjectRestoreAsync
    (
        ArchiveRestoreRequest request,
        CancellationToken cancellationToken = default
    );
}

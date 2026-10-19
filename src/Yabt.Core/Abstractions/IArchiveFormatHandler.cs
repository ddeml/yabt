using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

public interface IArchiveFormatHandler
{
    string FormatName { get; }

    bool ProjectsBesideSourceFolder { get; }

    bool CanRestoreArtifact(ArchiveProjectedObject artifact);

    /// <summary>Validates active source settings and reports options this handler ignores.</summary>
    void ValidateBackupPolicy(FolderPolicy policy, string? configurationSource = null) { }

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

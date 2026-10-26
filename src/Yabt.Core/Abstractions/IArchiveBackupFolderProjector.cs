using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

/// <summary>
/// Optionally exposes complete immediate folder representations without traversing
/// ordinary child folders. All work-unit paths are relative to the selected source root.
/// Applies to in-folder formats; beside-folder formats remain complete projections.
/// Newly constructed artifact groups must be complete within a unit. Pass-through
/// groups retain the source adapter's complete group evidence and ownership.
/// </summary>
public interface IArchiveBackupFolderProjector
{
    Task<ArchiveBackupWorkUnit> ProjectBackupFolderAsync
    (
        ArchiveProjectionRequest request,
        string relativeFolderPath,
        CancellationToken cancellationToken = default
    );
}

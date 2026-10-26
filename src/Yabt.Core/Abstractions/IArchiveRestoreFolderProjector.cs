using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

/// <summary>
/// Optional capability for reversing a complete folder's immediate plain
/// artifacts without inspecting descendants. Outputs remain in that folder;
/// child folders and complete packaged projections are owned by traversal.
/// </summary>
public interface IArchiveRestoreFolderProjector
{
    Task<ArchiveRestoreProjection> ProjectRestoreFolderAsync
    (
        IReadOnlyList<ArchiveProjectedObject> artifacts,
        string relativeFolderPath,
        CancellationToken cancellationToken = default
    );
}

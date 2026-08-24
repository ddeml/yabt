namespace Yabt.Core.Models;

public sealed class ArchiveRestoreRequest
{
    public ArchiveRestoreRequest
    (
        ArchiveProjectedObject artifact,
        bool restoreAsRoot = false,
        bool requireCompleteProjection = false
    ) : this([artifact], restoreAsRoot, requireCompleteProjection)
    {
    }

    public ArchiveRestoreRequest
    (
        IEnumerable<ArchiveProjectedObject> artifacts,
        bool restoreAsRoot = false,
        bool requireCompleteProjection = false
    )
    {
        ArgumentNullException.ThrowIfNull(artifacts);

        Artifacts = artifacts.ToArray();
        if (Artifacts.Count == 0)
        {
            throw new ArgumentException(
                "A restore projection request requires at least one artifact.",
                nameof(artifacts));
        }

        RestoreAsRoot = restoreAsRoot;
        RequireCompleteProjection = requireCompleteProjection;
    }

    public IReadOnlyList<ArchiveProjectedObject> Artifacts { get; }

    public bool RestoreAsRoot { get; }

    public bool RequireCompleteProjection { get; }

    public ArchiveProjectedObject Artifact => Artifacts.Count == 1 ?
        Artifacts[0] :
        throw new InvalidOperationException(
            "The single-artifact view is unavailable for a grouped restore request. " +
                "Format handlers must inspect Artifacts directly.");
}

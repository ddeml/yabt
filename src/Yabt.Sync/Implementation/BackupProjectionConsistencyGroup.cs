using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

internal sealed class BackupProjectionConsistencyGroup
{
    public ArchiveProjectionProvenance? Projection { get; init; }

    public int RemainingObjects { get; set; }

    public bool RequiresValidation { get; set; }

    public bool SourceReadFailed { get; set; }

    public List<BackupProjectionMatchedObject> MatchedObjects { get; } = [];
}

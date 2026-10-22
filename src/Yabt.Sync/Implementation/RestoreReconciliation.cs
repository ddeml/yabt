using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

internal sealed record RestoreReconciliation
(
    IReadOnlyList<ArchiveProjectedObject> FilesToWrite,
    IReadOnlyList<RestoreHistoryMove> HistoryMoves,
    IReadOnlySet<string> NewDesiredPaths,
    IReadOnlySet<string> ChangedDesiredPaths,
    IReadOnlySet<string> UnchangedDesiredPaths,
    int NewCount,
    int ChangedCount,
    int ExtraCount,
    int UnchangedCount,
    IReadOnlyList<SyncItemFailure> Failures
);

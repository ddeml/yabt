namespace Yabt.Sync.Implementation;

internal sealed record RestoreHistoryMove
(
    string RelativePath,
    bool IsFolder
);

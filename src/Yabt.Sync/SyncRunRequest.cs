namespace Yabt.Sync;

public sealed record SyncRunRequest
(
    string SourceRoot,
    bool DryRun = default
);

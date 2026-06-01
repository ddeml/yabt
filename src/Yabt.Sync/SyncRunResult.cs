namespace Yabt.Sync;

public sealed record SyncRunResult
(
    bool Completed,
    string Message,
    int NewCount = default,
    int ChangedCount = default,
    int ExtraCount = default,
    int UnchangedCount = default
);

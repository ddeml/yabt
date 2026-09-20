namespace Yabt.Sync;

public sealed record SyncItemFailure
(
    string RelativePath,
    string Operation,
    string Reason
);

namespace Yabt.Sync;

public sealed record HistoryDeduplicationRequest
(
    string ArchiveRoot,
    bool DryRun = default,
    string? TargetStoreId = default
);

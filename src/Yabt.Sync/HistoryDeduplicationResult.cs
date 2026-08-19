namespace Yabt.Sync;

public sealed record HistoryDeduplicationResult
(
    bool Completed,
    string Message,
    int ScannedObjectCount = default,
    int ExistingReferenceCount = default,
    int DuplicateGroupCount = default,
    int ReplacedObjectCount = default,
    int TinyObjectCount = default,
    long BytesSaved = default
);

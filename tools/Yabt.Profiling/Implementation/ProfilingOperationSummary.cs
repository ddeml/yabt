namespace Yabt.Profiling.Implementation;

/// <summary>Scalar results that do not retain any measured operation state.</summary>
internal sealed record ProfilingOperationSummary
(
    long ArchiveObjectCount,
    long PlannedFileCount,
    long PlannedDirectoryCount,
    long EmptyDirectoryCount,
    long ProjectionCount,
    long FilesToWriteCount,
    long HistoryMoveCount,
    long NewCount,
    long ChangedCount,
    long ExtraCount,
    long UnchangedCount
);

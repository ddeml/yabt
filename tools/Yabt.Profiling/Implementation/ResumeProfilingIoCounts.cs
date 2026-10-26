namespace Yabt.Profiling.Implementation;

internal sealed record ResumeProfilingIoCounts
(
    long ProviderObjectReadCalls,
    long ProviderFolderListCalls,
    long SourceObjectReadCalls,
    long ArchiveObjectReadCalls,
    long DestinationObjectReadCalls,
    long ZipEntryReadCalls,
    long ZipSourceReadCalls,
    long ProgressUnavailableWarnings
);

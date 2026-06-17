using Yabt.Common;

#pragma warning disable IDE0130 // Namespace does not match folder structure - Intentionally kept in the root namespace of the extended class for easier discoverability
namespace Microsoft.Extensions.Logging;
#pragma warning restore IDE0130 // Namespace does not match folder structure

internal static partial class ArchiveSynchronizerLogMessages
{
    [LoggerMessage(
        EventId = YabtEventIds.SyncRequested,
        Level = LogLevel.Information,
        Message = "Sync requested for {SourceRoot}. DryRun={DryRun}")]
    public static partial void LogSyncRequested(
        this ILogger logger,
        string sourceRoot,
        bool dryRun);

    [LoggerMessage(
        EventId = YabtEventIds.ArchiveSyncCompleted,
        Level = LogLevel.Information,
        Message = "Archive {OperationName} completed. New={NewCount}; Changed={ChangedCount}; Extra={ExtraCount}; Unchanged={UnchangedCount}.")]
    public static partial void LogArchiveSyncCompleted
    (
        this ILogger logger,
        string operationName,
        int newCount,
        int changedCount,
        int extraCount,
        int unchangedCount
    );

    [LoggerMessage(
        EventId = YabtEventIds.MultipleTargetStoresWithoutSelection,
        Level = LogLevel.Warning,
        Message = "Backup root {ArchiveId} defines multiple target stores and no target store id was specified; using first store {StoreId}.")]
    public static partial void LogMultipleTargetStoresWithoutSelection
    (
        this ILogger logger,
        string archiveId,
        string storeId
    );
}

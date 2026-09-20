using Yabt.Common;

#pragma warning disable IDE0130 // Namespace does not match folder structure - Intentionally kept in the root namespace of the extended class for easier discoverability
namespace Microsoft.Extensions.Logging;
#pragma warning restore IDE0130 // Namespace does not match folder structure

internal static partial class ArchiveSynchronizerLogMessages
{
    [LoggerMessage(
        EventId = YabtEventIds.BackupRequested,
        Level = LogLevel.Information,
        Message = "Backup requested for {SourceRoot}. DryRun={DryRun}")]
    public static partial void LogBackupRequested(
        this ILogger logger,
        string sourceRoot,
        bool dryRun);

    [LoggerMessage(
        EventId = YabtEventIds.RestoreRequested,
        Level = LogLevel.Information,
        Message = "Restore requested from {SourceRoot} to {DestinationRoot}. DryRun={DryRun}; MaximumConcurrency={MaximumConcurrency}.")]
    public static partial void LogRestoreRequested
    (
        this ILogger logger,
        string sourceRoot,
        string destinationRoot,
        bool dryRun,
        int maximumConcurrency
    );

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
        EventId = YabtEventIds.ArchiveSyncIncomplete,
        Level = LogLevel.Warning,
        Message = "Archive {OperationName} was incomplete because {FailureCount} item(s) could not be read safely.")]
    public static partial void LogArchiveSyncIncomplete
    (
        this ILogger logger,
        string operationName,
        int failureCount
    );

    [LoggerMessage(
        EventId = YabtEventIds.SyncItemReadFailed,
        Level = LogLevel.Warning,
        Message = "Could not read {RelativePath} during {Operation}: {Reason}")]
    public static partial void LogSyncItemReadFailed
    (
        this ILogger logger,
        string relativePath,
        string operation,
        string reason
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

    [LoggerMessage(
        EventId = YabtEventIds.InvalidChangeManifestIgnored,
        Level = LogLevel.Warning,
        Message = "Change manifest {ManifestKey} is invalid and will not be trusted. A mutating backup will replace it after a full comparison.")]
    public static partial void LogInvalidChangeManifestIgnored
    (
        this ILogger logger,
        string manifestKey,
        Exception exception
    );

    [LoggerMessage(
        EventId = YabtEventIds.IgnoringRestoreTemporaryPathDeleteException,
        Level = LogLevel.Debug,
        Message = "Could not remove restore temporary path {TemporaryPath}.")]
    public static partial void LogIgnoringRestoreTemporaryPathDeleteException
    (
        this ILogger logger,
        Exception exception,
        string temporaryPath
    );

    [LoggerMessage(
        EventId = YabtEventIds.ArchiveObjectUnchanged,
        Level = LogLevel.Debug,
        Message = "{OperationName}: user-data object {RelativePath} is unchanged.")]
    public static partial void LogArchiveObjectUnchanged(
        this ILogger logger,
        string operationName,
        string relativePath);

    [LoggerMessage(
        EventId = YabtEventIds.BackupObjectAdded,
        Level = LogLevel.Information,
        Message = "Added user-data object {RelativePath} to the archive.")]
    public static partial void LogBackupObjectAdded(
        this ILogger logger,
        string relativePath);

    [LoggerMessage(
        EventId = YabtEventIds.BackupObjectChanged,
        Level = LogLevel.Information,
        Message = "Changed archived user-data object {RelativePath}.")]
    public static partial void LogBackupObjectChanged(
        this ILogger logger,
        string relativePath);

    [LoggerMessage(
        EventId = YabtEventIds.BackupObjectHistorized,
        Level = LogLevel.Information,
        Message = "Moved the previous user-data object {RelativePath} to history ({Reason}).")]
    public static partial void LogBackupObjectHistorized(
        this ILogger logger,
        string relativePath,
        string reason);

    [LoggerMessage(
        EventId = YabtEventIds.BackupWouldAddObject,
        Level = LogLevel.Information,
        Message = "Would add user-data object {RelativePath} to the archive.")]
    public static partial void LogBackupWouldAddObject(
        this ILogger logger,
        string relativePath);

    [LoggerMessage(
        EventId = YabtEventIds.BackupWouldChangeObject,
        Level = LogLevel.Information,
        Message = "Would change archived user-data object {RelativePath}.")]
    public static partial void LogBackupWouldChangeObject(
        this ILogger logger,
        string relativePath);

    [LoggerMessage(
        EventId = YabtEventIds.BackupWouldHistorizeObject,
        Level = LogLevel.Information,
        Message = "Would move user-data object {RelativePath} to history ({Reason}).")]
    public static partial void LogBackupWouldHistorizeObject(
        this ILogger logger,
        string relativePath,
        string reason);

    [LoggerMessage(
        EventId = YabtEventIds.VerifyDifference,
        Level = LogLevel.Information,
        Message = "Verify difference for user-data object {RelativePath}: {Difference}.")]
    public static partial void LogVerifyDifference(
        this ILogger logger,
        string relativePath,
        string difference);

    [LoggerMessage(
        EventId = YabtEventIds.RestoreItemWritten,
        Level = LogLevel.Information,
        Message = "Restored {ChangeKind} {ItemKind} {RelativePath}.")]
    public static partial void LogRestoreItemWritten(
        this ILogger logger,
        string changeKind,
        string itemKind,
        string relativePath);

    [LoggerMessage(
        EventId = YabtEventIds.RestoreWouldWriteItem,
        Level = LogLevel.Information,
        Message = "Would restore {ChangeKind} {ItemKind} {RelativePath}.")]
    public static partial void LogRestoreWouldWriteItem(
        this ILogger logger,
        string changeKind,
        string itemKind,
        string relativePath);

    [LoggerMessage(
        EventId = YabtEventIds.RestoreItemHistorized,
        Level = LogLevel.Information,
        Message = "Moved existing user-data {ItemKind} {RelativePath} to restore history.")]
    public static partial void LogRestoreItemHistorized(
        this ILogger logger,
        string itemKind,
        string relativePath);

    [LoggerMessage(
        EventId = YabtEventIds.RestoreWouldHistorizeItem,
        Level = LogLevel.Information,
        Message = "Would move existing user-data {ItemKind} {RelativePath} to restore history.")]
    public static partial void LogRestoreWouldHistorizeItem(
        this ILogger logger,
        string itemKind,
        string relativePath);

    [LoggerMessage(
        EventId = YabtEventIds.RestoreItemUnchanged,
        Level = LogLevel.Debug,
        Message = "Restore: user-data {ItemKind} {RelativePath} is unchanged.")]
    public static partial void LogRestoreItemUnchanged(
        this ILogger logger,
        string itemKind,
        string relativePath);

    [LoggerMessage(
        EventId = YabtEventIds.ControlMetadataOperation,
        Level = LogLevel.Debug,
        Message = "{Action} YABT control metadata {Path}.")]
    public static partial void LogControlMetadataOperation(
        this ILogger logger,
        string action,
        string path);

    [LoggerMessage(
        EventId = YabtEventIds.ObjectRead,
        Level = LogLevel.Debug,
        Message = "Reading user-data object {RelativePath} for {Purpose}.")]
    public static partial void LogObjectRead(
        this ILogger logger,
        string relativePath,
        string purpose);

    [LoggerMessage(
        EventId = YabtEventIds.EmptyDirectoryUnchanged,
        Level = LogLevel.Debug,
        Message = "{OperationName}: logical empty directory {RelativePath} is unchanged.")]
    public static partial void LogEmptyDirectoryUnchanged(
        this ILogger logger,
        string operationName,
        string relativePath);

    [LoggerMessage(
        EventId = YabtEventIds.BackupEmptyDirectoryCreated,
        Level = LogLevel.Information,
        Message = "Created logical empty directory {RelativePath} in the archive.")]
    public static partial void LogBackupEmptyDirectoryCreated(
        this ILogger logger,
        string relativePath);

    [LoggerMessage(
        EventId = YabtEventIds.BackupEmptyDirectoryChanged,
        Level = LogLevel.Information,
        Message = "Changed logical directory {RelativePath} to or from its empty representation.")]
    public static partial void LogBackupEmptyDirectoryChanged(
        this ILogger logger,
        string relativePath);

    [LoggerMessage(
        EventId = YabtEventIds.BackupEmptyDirectoryRemoved,
        Level = LogLevel.Information,
        Message = "Removed logical empty directory {RelativePath} from the live archive and moved it to history.")]
    public static partial void LogBackupEmptyDirectoryRemoved(
        this ILogger logger,
        string relativePath);

    [LoggerMessage(
        EventId = YabtEventIds.BackupWouldCreateEmptyDirectory,
        Level = LogLevel.Information,
        Message = "Would create logical empty directory {RelativePath} in the archive.")]
    public static partial void LogBackupWouldCreateEmptyDirectory(
        this ILogger logger,
        string relativePath);

    [LoggerMessage(
        EventId = YabtEventIds.BackupWouldChangeEmptyDirectory,
        Level = LogLevel.Information,
        Message = "Would change logical directory {RelativePath} to or from its empty representation.")]
    public static partial void LogBackupWouldChangeEmptyDirectory(
        this ILogger logger,
        string relativePath);

    [LoggerMessage(
        EventId = YabtEventIds.BackupWouldRemoveEmptyDirectory,
        Level = LogLevel.Information,
        Message = "Would remove logical empty directory {RelativePath} from the live archive and move it to history.")]
    public static partial void LogBackupWouldRemoveEmptyDirectory(
        this ILogger logger,
        string relativePath);

    [LoggerMessage(
        EventId = YabtEventIds.VerifyEmptyDirectoryDifference,
        Level = LogLevel.Information,
        Message = "Verify difference for logical empty directory {RelativePath}: {Difference}.")]
    public static partial void LogVerifyEmptyDirectoryDifference(
        this ILogger logger,
        string relativePath,
        string difference);
}

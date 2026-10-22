using Microsoft.Extensions.Logging;
using Yabt.Core.Models;
using Yabt.Metadata;

namespace Yabt.Sync.Implementation;

internal static class ArchiveOperationLogging
{
    internal static void ReportBackupObjectUnchanged
    (
        this ILogger _logger,
        string operationName,
        string relativePath,
        ArchiveProjectionProvenance? projection
    )
    {
        if (!_logger.IsEnabled(LogLevel.Debug)) { return; }

        if (ArchivePathRules.IsEmptyFolderMarker(relativePath))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogControlMetadataOperation(
                    $"{operationName}: determined unchanged",
                    relativePath);
            }
            _logger.LogEmptyDirectoryUnchanged(
                operationName,
                ArchivePathRules.GetLogicalEmptyDirectoryPath(relativePath));
            return;
        }
        else if (ArchivePathRules.IsYabtControlMetadataPath(relativePath, projection: projection))
        {
            if (!_logger.IsEnabled(LogLevel.Debug)) { return; }
            _logger.LogControlMetadataOperation(
                $"{operationName}: determined unchanged",
                relativePath);
            return;
        }

        _logger.LogArchiveObjectUnchanged(operationName, relativePath);
    }

    internal static void ReportBackupObjectAdded
    (
        this ILogger _logger,
        string relativePath,
        bool existingDirectoryChanged = false,
        ArchiveProjectionProvenance? projection = null
    )
    {
        if (ArchivePathRules.IsEmptyFolderMarker(relativePath))
        {
            _logger.LogControlMetadataOperation("Wrote", relativePath);
            if (existingDirectoryChanged)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogBackupEmptyDirectoryChanged(
                        ArchivePathRules.GetLogicalEmptyDirectoryPath(relativePath));
                }
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogBackupEmptyDirectoryCreated(
                        ArchivePathRules.GetLogicalEmptyDirectoryPath(relativePath));
                }
            }
            return;
        }
        else if (ArchivePathRules.IsYabtControlMetadataPath(relativePath, projection: projection))
        {
            _logger.LogControlMetadataOperation("Wrote", relativePath);
            return;
        }

        _logger.LogBackupObjectAdded(relativePath);
    }

    internal static void ReportBackupObjectChanged
    (
        this ILogger _logger,
        string relativePath,
        ArchiveProjectionProvenance? projection
    )
    {
        if (ArchivePathRules.IsEmptyFolderMarker(relativePath))
        {
            _logger.LogControlMetadataOperation("Replaced", relativePath);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogEmptyDirectoryUnchanged(
                    "backup marker repair",
                    ArchivePathRules.GetLogicalEmptyDirectoryPath(relativePath));
            }
            return;
        }
        else if (ArchivePathRules.IsYabtControlMetadataPath(relativePath, projection: projection))
        {
            _logger.LogControlMetadataOperation("Replaced", relativePath);
            return;
        }

        _logger.LogBackupObjectChanged(relativePath);
    }

    internal static void ReportBackupObjectHistorized
    (
        this ILogger _logger,
        string relativePath,
        string reason,
        bool isDirectory = false,
        ArchiveProjectionProvenance? projection = null
    )
    {
        if (ArchivePathRules.IsEmptyFolderMarker(relativePath))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogControlMetadataOperation(
                    $"Moved to history ({reason})",
                    relativePath);
            }
            if (string.Equals(
                    reason,
                    "empty directory changed",
                    StringComparison.Ordinal))
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogBackupEmptyDirectoryChanged(
                        ArchivePathRules.GetLogicalEmptyDirectoryPath(relativePath));
                }
            }
            else if (string.Equals(
                         reason,
                         "removed from the source",
                         StringComparison.Ordinal))
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogBackupEmptyDirectoryRemoved(
                        ArchivePathRules.GetLogicalEmptyDirectoryPath(relativePath));
                }
            }
            return;
        }
        else if (ArchivePathRules.IsYabtControlMetadataPath(relativePath, isDirectory, projection))
        {
            if (!_logger.IsEnabled(LogLevel.Debug)) { return; }
            _logger.LogControlMetadataOperation(
                $"Moved to history ({reason})",
                relativePath);
            return;
        }

        _logger.LogBackupObjectHistorized(relativePath, reason);
    }

    internal static void ReportBackupWouldAddObject
    (
        this ILogger _logger,
        string relativePath,
        bool existingDirectoryChanged = false,
        ArchiveProjectionProvenance? projection = null
    )
    {
        if (ArchivePathRules.IsEmptyFolderMarker(relativePath))
        {
            _logger.LogControlMetadataOperation("Would write", relativePath);
            if (existingDirectoryChanged)
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogBackupWouldChangeEmptyDirectory(
                        ArchivePathRules.GetLogicalEmptyDirectoryPath(relativePath));
                }
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogBackupWouldCreateEmptyDirectory(
                        ArchivePathRules.GetLogicalEmptyDirectoryPath(relativePath));
                }
            }
            return;
        }
        else if (ArchivePathRules.IsYabtControlMetadataPath(relativePath, projection: projection))
        {
            _logger.LogControlMetadataOperation("Would write", relativePath);
            return;
        }

        _logger.LogBackupWouldAddObject(relativePath);
    }

    internal static void ReportBackupWouldChangeObject
    (
        this ILogger _logger,
        string relativePath,
        ArchiveProjectionProvenance? projection
    )
    {
        if (ArchivePathRules.IsEmptyFolderMarker(relativePath))
        {
            _logger.LogControlMetadataOperation("Would replace", relativePath);
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogEmptyDirectoryUnchanged(
                    "backup dry run marker repair",
                    ArchivePathRules.GetLogicalEmptyDirectoryPath(relativePath));
            }
            return;
        }
        else if (ArchivePathRules.IsYabtControlMetadataPath(relativePath, projection: projection))
        {
            _logger.LogControlMetadataOperation("Would replace", relativePath);
            return;
        }

        _logger.LogBackupWouldChangeObject(relativePath);
    }

    internal static void ReportBackupWouldHistorizeObject
    (
        this ILogger _logger,
        string relativePath,
        string reason,
        bool isDirectory = false,
        ArchiveProjectionProvenance? projection = null
    )
    {
        if (ArchivePathRules.IsEmptyFolderMarker(relativePath))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogControlMetadataOperation(
                    $"Would move to history ({reason})",
                    relativePath);
            }
            if (string.Equals(
                    reason,
                    "empty directory changed",
                    StringComparison.Ordinal))
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogBackupWouldChangeEmptyDirectory(
                        ArchivePathRules.GetLogicalEmptyDirectoryPath(relativePath));
                }
            }
            else if (string.Equals(
                         reason,
                         "removed from the source",
                         StringComparison.Ordinal))
            {
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogBackupWouldRemoveEmptyDirectory(
                        ArchivePathRules.GetLogicalEmptyDirectoryPath(relativePath));
                }
            }
            return;
        }
        else if (ArchivePathRules.IsYabtControlMetadataPath(relativePath, isDirectory, projection))
        {
            if (!_logger.IsEnabled(LogLevel.Debug)) { return; }
            _logger.LogControlMetadataOperation(
                $"Would move to history ({reason})",
                relativePath);
            return;
        }

        _logger.LogBackupWouldHistorizeObject(relativePath, reason);
    }

    internal static void ReportVerifyObjectDifference
    (
        this ILogger _logger,
        string relativePath,
        string difference,
        bool isDirectory = false,
        ArchiveProjectionProvenance? projection = null
    )
    {
        if (ArchivePathRules.IsEmptyFolderMarker(relativePath))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogControlMetadataOperation(
                    $"Verify found difference ({difference}) for",
                    relativePath);
            }
            if (string.Equals(
                    difference,
                    "content differs",
                    StringComparison.Ordinal))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogEmptyDirectoryUnchanged(
                        "verify marker content",
                        ArchivePathRules.GetLogicalEmptyDirectoryPath(relativePath));
                }
            }
            else if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogVerifyEmptyDirectoryDifference(
                    ArchivePathRules.GetLogicalEmptyDirectoryPath(relativePath),
                    difference);
            }
            return;
        }
        else if (ArchivePathRules.IsYabtControlMetadataPath(relativePath, isDirectory, projection))
        {
            if (!_logger.IsEnabled(LogLevel.Debug)) { return; }
            _logger.LogControlMetadataOperation(
                $"Verify found difference ({difference}) for",
                relativePath);
            return;
        }

        _logger.LogVerifyDifference(relativePath, difference);
    }

    internal static void ReportRestoreDryRun
    (
        this ILogger _logger,
        RestorePlan plan,
        RestoreReconciliation reconciliation,
        RootDescriptorRestore rootDescriptorRestore,
        bool restoreRootNeedsCreation
    )
    {
        if (restoreRootNeedsCreation)
        {
            _logger.ReportRestoreWouldWriteItem("new", "directory", ".");
        }

        foreach (var historyMove in reconciliation.HistoryMoves)
        {
            _logger.ReportRestoreWouldHistorizeItem(historyMove);
        }

        foreach (var relativePath in reconciliation.NewDesiredPaths)
        {
            _logger.ReportRestoreWouldWriteItem(
                "new",
                GetRestoreItemKind(plan, relativePath),
                relativePath);
        }

        foreach (var relativePath in reconciliation.ChangedDesiredPaths)
        {
            _logger.ReportRestoreWouldWriteItem(
                "changed",
                GetRestoreItemKind(plan, relativePath),
                relativePath);
        }

        foreach (var relativePath in reconciliation.UnchangedDesiredPaths)
        {
            _logger.ReportRestoreItemUnchanged(
                GetRestoreItemKind(plan, relativePath),
                relativePath);
        }

        if (rootDescriptorRestore.MoveExistingToHistory)
        {
            _logger.LogControlMetadataOperation(
                "Would move to restore history",
                BackupRootFileNames.Primary);
        }
        if (rootDescriptorRestore.NeedsWrite)
        {
            _logger.LogControlMetadataOperation(
                "Would write",
                BackupRootFileNames.Primary);
        }
    }

    internal static void ReportRestoreItemWritten
    (
        this ILogger _logger,
        string changeKind,
        string itemKind,
        string relativePath
    )
    {
        if (ArchivePathRules.IsYabtControlMetadataPath(
                relativePath,
                string.Equals(itemKind, "directory", StringComparison.Ordinal)))
        {
            if (!_logger.IsEnabled(LogLevel.Debug)) { return; }
            _logger.LogControlMetadataOperation(
                $"Restored {changeKind} {itemKind}",
                relativePath);
            return;
        }

        _logger.LogRestoreItemWritten(changeKind, itemKind, relativePath);
    }

    internal static void ReportRestoreWouldWriteItem
    (
        this ILogger _logger,
        string changeKind,
        string itemKind,
        string relativePath
    )
    {
        if (ArchivePathRules.IsYabtControlMetadataPath(
                relativePath,
                string.Equals(itemKind, "directory", StringComparison.Ordinal)))
        {
            if (!_logger.IsEnabled(LogLevel.Debug)) { return; }
            _logger.LogControlMetadataOperation(
                $"Would restore {changeKind} {itemKind}",
                relativePath);
            return;
        }

        _logger.LogRestoreWouldWriteItem(changeKind, itemKind, relativePath);
    }

    internal static void ReportRestoreItemHistorized(this ILogger _logger, RestoreHistoryMove historyMove)
    {
        var itemKind = historyMove.IsFolder ? "directory" : "file";
        if (ArchivePathRules.IsYabtControlMetadataPath(
                historyMove.RelativePath,
                historyMove.IsFolder))
        {
            if (!_logger.IsEnabled(LogLevel.Debug)) { return; }
            _logger.LogControlMetadataOperation(
                $"Moved existing {itemKind} to restore history",
                historyMove.RelativePath);
            return;
        }

        _logger.LogRestoreItemHistorized(itemKind, historyMove.RelativePath);
    }

    internal static void ReportRestoreWouldHistorizeItem(this ILogger _logger, RestoreHistoryMove historyMove)
    {
        var itemKind = historyMove.IsFolder ? "directory" : "file";
        if (ArchivePathRules.IsYabtControlMetadataPath(
                historyMove.RelativePath,
                historyMove.IsFolder))
        {
            if (!_logger.IsEnabled(LogLevel.Debug)) { return; }
            _logger.LogControlMetadataOperation(
                $"Would move existing {itemKind} to restore history",
                historyMove.RelativePath);
            return;
        }

        _logger.LogRestoreWouldHistorizeItem(itemKind, historyMove.RelativePath);
    }

    internal static void ReportRestoreItemUnchanged(this ILogger _logger, string itemKind, string relativePath)
    {
        if (!_logger.IsEnabled(LogLevel.Debug)) { return; }

        if (ArchivePathRules.IsYabtControlMetadataPath(
                relativePath,
                string.Equals(itemKind, "directory", StringComparison.Ordinal)))
        {
            if (!_logger.IsEnabled(LogLevel.Debug)) { return; }
            _logger.LogControlMetadataOperation(
                $"Restore determined {itemKind} unchanged",
                relativePath);
            return;
        }

        _logger.LogRestoreItemUnchanged(itemKind, relativePath);
    }

    internal static void ReportObjectReadOperation(this ILogger _logger, string relativePath, string purpose)
    {
        if (!_logger.IsEnabled(LogLevel.Debug)) { return; }

        if (ArchivePathRules.IsYabtControlMetadataPath(relativePath))
        {
            if (!_logger.IsEnabled(LogLevel.Debug)) { return; }
            _logger.LogControlMetadataOperation(
                $"Reading for {purpose}",
                relativePath);
            return;
        }

        _logger.LogObjectRead(relativePath, purpose);
    }

    internal static string GetRestoreChangeKind
    (
        RestoreReconciliation reconciliation,
        string relativePath
    ) => reconciliation.ChangedDesiredPaths.Contains(relativePath) ? "changed" : "new";

    internal static string GetRestoreItemKind(RestorePlan plan, string relativePath) =>
        plan.Directories.Contains(relativePath, plan.Namespace.PathComparer) ?
            "directory" :
            "file";

    internal static SyncItemFailure CreateReadFailure
    (
        this ILogger _logger,
        string relativePath,
        string operation,
        Exception exception
    )
    {
        var failure = new SyncItemFailure(
            relativePath,
            operation,
            SyncFailureReporting.GetFailureReason(exception));
        _logger.LogSyncItemReadFailed(
            failure.RelativePath,
            failure.Operation,
            failure.Reason);
        return failure;
    }
}

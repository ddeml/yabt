using Microsoft.Extensions.Logging;
using Yabt.Common;

namespace Yabt.Metadata.Implementation;

internal static partial class MetadataFileLogMessages
{
    [LoggerMessage(
        EventId = YabtEventIds.BackupRootStartPathCheck,
        Level = LogLevel.Debug,
        Message = "Checking whether backup-root lookup start path {StartPath} is a file.")]
    public static partial void LogBackupRootStartPathCheck
    (
        this ILogger logger,
        string startPath
    );

    [LoggerMessage(
        EventId = YabtEventIds.BackupRootDescriptorCheck,
        Level = LogLevel.Debug,
        Message = "Checking for backup-root descriptor {DescriptorPath}.")]
    public static partial void LogBackupRootDescriptorCheck
    (
        this ILogger logger,
        string descriptorPath
    );

    [LoggerMessage(
        EventId = YabtEventIds.BackupRootDescriptorRead,
        Level = LogLevel.Debug,
        Message = "Reading backup-root descriptor {DescriptorPath}.")]
    public static partial void LogBackupRootDescriptorRead
    (
        this ILogger logger,
        string descriptorPath
    );

    [LoggerMessage(
        EventId = YabtEventIds.FolderPolicyCheck,
        Level = LogLevel.Debug,
        Message = "Checking for folder policy {PolicyPath}.")]
    public static partial void LogFolderPolicyCheck
    (
        this ILogger logger,
        string policyPath
    );

    [LoggerMessage(
        EventId = YabtEventIds.FolderPolicyRead,
        Level = LogLevel.Debug,
        Message = "Reading folder policy {PolicyPath}.")]
    public static partial void LogFolderPolicyRead
    (
        this ILogger logger,
        string policyPath
    );

    [LoggerMessage(
        EventId = YabtEventIds.FolderPolicyDefault,
        Level = LogLevel.Debug,
        Message = "Folder policy {PolicyPath} does not exist; using the default mirror policy.")]
    public static partial void LogFolderPolicyDefault
    (
        this ILogger logger,
        string policyPath
    );
}

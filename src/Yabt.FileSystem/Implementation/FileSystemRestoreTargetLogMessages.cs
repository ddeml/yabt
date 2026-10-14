using Yabt.Common;

#pragma warning disable IDE0130
namespace Microsoft.Extensions.Logging;
#pragma warning restore IDE0130

internal static partial class FileSystemRestoreTargetLogMessages
{
    [LoggerMessage
    (
        EventId = YabtEventIds.ControlMetadataOperation,
        Level = LogLevel.Debug,
        Message = "{Action} YABT control metadata {Path}."
    )]
    public static partial void LogRestoreDestinationControlMetadataOperation
    (
        this ILogger logger,
        string action,
        string path
    );

    [LoggerMessage
    (
        EventId = YabtEventIds.ObjectRead,
        Level = LogLevel.Debug,
        Message = "Reading user-data object {RelativePath} for {Purpose}."
    )]
    public static partial void LogRestoreDestinationObjectRead
    (
        this ILogger logger,
        string relativePath,
        string purpose
    );

    [LoggerMessage
    (
        EventId = YabtEventIds.IgnoringRestoreTemporaryPathDeleteException,
        Level = LogLevel.Debug,
        Message = "Could not remove restore temporary path {TemporaryPath}."
    )]
    public static partial void LogRestoreDestinationTemporaryCleanupFailed
    (
        this ILogger logger,
        Exception exception,
        string temporaryPath
    );
}

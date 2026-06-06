using Yabt.Common;

namespace Microsoft.Extensions.Logging;

internal static partial class FileSystemObjectStoreLogMessages
{
    [LoggerMessage(
        EventId = YabtEventIds.IgnoringTemporaryObjectDeleteException,
        Level = LogLevel.Debug,
        Message = "Ignoring exception while deleting temporary filesystem object {Path}.")]
    public static partial void LogIgnoringTemporaryObjectDeleteException
    (
        this ILogger logger,
        Exception exception,
        string path
    );

    [LoggerMessage(
        EventId = YabtEventIds.AbandonedFileSystemOperationFailed,
        Level = LogLevel.Warning,
        Message = "Abandoned filesystem operation {Operation} for {Path} failed after cancellation.")]
    public static partial void LogAbandonedFileSystemOperationFailed
    (
        this ILogger logger,
        Exception exception,
        string operation,
        string path
    );

    [LoggerMessage(
        EventId = YabtEventIds.IgnoringListEnumeratorDisposeException,
        Level = LogLevel.Debug,
        Message = "Ignoring exception while disposing filesystem list enumerator for {Path}.")]
    public static partial void LogIgnoringListEnumeratorDisposeException
    (
        this ILogger logger,
        Exception exception,
        string path
    );

    [LoggerMessage(
        EventId = YabtEventIds.IgnoringAbandonedListChunkException,
        Level = LogLevel.Debug,
        Message = "Ignoring abandoned filesystem list chunk exception for {Path}.")]
    public static partial void LogIgnoringAbandonedListChunkException
    (
        this ILogger logger,
        Exception exception,
        string path
    );

}

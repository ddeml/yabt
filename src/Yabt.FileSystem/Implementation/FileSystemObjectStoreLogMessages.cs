using Yabt.Common;

namespace Microsoft.Extensions.Logging;

internal static partial class FileSystemObjectStoreLogMessages
{
    [LoggerMessage(
        EventId = YabtEventIds.FileSystemStoreReady,
        Level = LogLevel.Debug,
        Message = "Ensuring filesystem object-store root {RootPath} exists.")]
    public static partial void LogFileSystemStoreReady
    (
        this ILogger logger,
        string rootPath
    );

    [LoggerMessage(
        EventId = YabtEventIds.FileSystemObjectUpload,
        Level = LogLevel.Debug,
        Message = "Uploading filesystem object {ObjectKey} to {Path}.")]
    public static partial void LogFileSystemObjectUpload
    (
        this ILogger logger,
        string objectKey,
        string path
    );

    [LoggerMessage(
        EventId = YabtEventIds.FileSystemObjectRead,
        Level = LogLevel.Debug,
        Message = "Opening filesystem object {ObjectKey} at {Path} for reading.")]
    public static partial void LogFileSystemObjectRead
    (
        this ILogger logger,
        string objectKey,
        string path
    );

    [LoggerMessage(
        EventId = YabtEventIds.FileSystemObjectConditionalReplace,
        Level = LogLevel.Debug,
        Message = "Attempting conditional replacement of filesystem object {ObjectKey} at {Path}.")]
    public static partial void LogFileSystemObjectConditionalReplace
    (
        this ILogger logger,
        string objectKey,
        string path
    );

    [LoggerMessage(
        EventId = YabtEventIds.FileSystemObjectConditionalDelete,
        Level = LogLevel.Debug,
        Message = "Attempting conditional deletion of filesystem object {ObjectKey} at {Path}.")]
    public static partial void LogFileSystemObjectConditionalDelete
    (
        this ILogger logger,
        string objectKey,
        string path
    );

    [LoggerMessage(
        EventId = YabtEventIds.FileSystemObjectExists,
        Level = LogLevel.Debug,
        Message = "Checking whether filesystem object {ObjectKey} exists at {Path}.")]
    public static partial void LogFileSystemObjectExists
    (
        this ILogger logger,
        string objectKey,
        string path
    );

    [LoggerMessage(
        EventId = YabtEventIds.FileSystemFolderList,
        Level = LogLevel.Debug,
        Message = "Listing filesystem folder prefix {FolderPrefix} at {Path}. Recursive={Recursive}")]
    public static partial void LogFileSystemFolderList
    (
        this ILogger logger,
        string folderPrefix,
        string path,
        bool recursive
    );

    [LoggerMessage(
        EventId = YabtEventIds.FileSystemObjectMove,
        Level = LogLevel.Debug,
        Message = "Moving filesystem object {SourceKey} to {DestinationKey}.")]
    public static partial void LogFileSystemObjectMove
    (
        this ILogger logger,
        string sourceKey,
        string destinationKey
    );

    [LoggerMessage(
        EventId = YabtEventIds.FileSystemFolderMove,
        Level = LogLevel.Debug,
        Message = "Moving filesystem folder prefix {SourcePrefix} to {DestinationPrefix}.")]
    public static partial void LogFileSystemFolderMove
    (
        this ILogger logger,
        string sourcePrefix,
        string destinationPrefix
    );

    [LoggerMessage(
        EventId = YabtEventIds.FileSystemConditionalMutationRead,
        Level = LogLevel.Debug,
        Message = "Reading filesystem object at {Path} to validate a conditional mutation.")]
    public static partial void LogFileSystemConditionalMutationRead
    (
        this ILogger logger,
        string path
    );

    [LoggerMessage(
        EventId = YabtEventIds.FileSystemArchiveMutationLockAcquire,
        Level = LogLevel.Debug,
        Message = "Acquiring filesystem archive mutation lock at {Path}.")]
    public static partial void LogFileSystemArchiveMutationLockAcquire
    (
        this ILogger logger,
        string path
    );

    [LoggerMessage(
        EventId = YabtEventIds.FileSystemPlumbingOperation,
        Level = LogLevel.Debug,
        Message = "{Action} filesystem provider plumbing at {Path}.")]
    public static partial void LogFileSystemPlumbingOperation
    (
        this ILogger logger,
        string action,
        string path
    );

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

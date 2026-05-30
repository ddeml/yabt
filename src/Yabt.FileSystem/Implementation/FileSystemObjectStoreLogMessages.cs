namespace Microsoft.Extensions.Logging;

internal static partial class FileSystemObjectStoreLogMessages
{
    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Debug,
        Message = "Ignoring expected IO exception while deleting temporary filesystem object {Path}.")]
    public static partial void LogIgnoringTemporaryObjectIoDeleteException
    (
        this ILogger logger,
        Exception exception,
        string path
    );

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Debug,
        Message = "Ignoring expected access exception while deleting temporary filesystem object {Path}.")]
    public static partial void LogIgnoringTemporaryObjectAccessDeleteException
    (
        this ILogger logger,
        Exception exception,
        string path
    );
}

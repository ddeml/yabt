using Microsoft.Extensions.Logging;
using Yabt.Common;

namespace Yabt.Format.Zip.Implementation;

internal static partial class ZipArchiveFormatHandlerLogMessages
{
    [LoggerMessage(
        EventId = YabtEventIds.IgnoringZipRestoreTemporaryPathDeleteException,
        Level = LogLevel.Debug,
        Message = "Ignoring exception while deleting ZIP restore temporary path {Path}.")]
    public static partial void LogIgnoringZipRestoreTemporaryPathDeleteException
    (
        this ILogger logger,
        Exception exception,
        string path
    );
}

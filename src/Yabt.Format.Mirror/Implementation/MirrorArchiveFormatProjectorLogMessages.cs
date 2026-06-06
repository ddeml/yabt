using Microsoft.Extensions.Logging;
using Yabt.Common;

namespace Yabt.Format.Mirror.Implementation;

internal static partial class MirrorArchiveFormatProjectorLogMessages
{
    [LoggerMessage(
        EventId = YabtEventIds.MirrorProjectedObject,
        Level = LogLevel.Debug,
        Message = "Mirror projection added source object {SourceKey} as {RelativePath}.")]
    public static partial void LogMirrorProjectedObject
    (
        this ILogger logger,
        string sourceKey,
        string relativePath
    );

    [LoggerMessage(
        EventId = YabtEventIds.MirrorProjectionCompleted,
        Level = LogLevel.Information,
        Message = "Mirror projection completed with {ObjectCount} object(s).")]
    public static partial void LogMirrorProjectionCompleted
    (
        this ILogger logger,
        int objectCount
    );
}

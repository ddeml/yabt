using Microsoft.Extensions.Logging;
using Yabt.Common;

namespace Yabt.Sync.Implementation;

internal static partial class OperationProgressLogMessages
{
    [LoggerMessage(EventId = YabtEventIds.OperationProgressUnavailable, Level = LogLevel.Warning,
        Message = "Local operation progress is unavailable; continuing with fresh discovery and validation.")]
    public static partial void LogOperationProgressUnavailable(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = YabtEventIds.OperationProgressResumed, Level = LogLevel.Information,
        Message = "Using local progress to prioritize unfinished work; all completed work will be revalidated.")]
    public static partial void LogOperationProgressResumed(this ILogger logger);

    [LoggerMessage(EventId = YabtEventIds.OperationProgressCleanupFailed, Level = LogLevel.Debug,
        Message = "Could not finish releasing the disposable local progress journal.")]
    public static partial void LogOperationProgressCleanupFailed(this ILogger logger, Exception exception);
}

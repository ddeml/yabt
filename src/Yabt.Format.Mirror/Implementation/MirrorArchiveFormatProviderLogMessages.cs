using Microsoft.Extensions.Logging;

namespace Yabt.Format.Mirror.Implementation;

internal static partial class MirrorArchiveFormatProviderLogMessages
{
    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Debug,
        Message = "Mirror {OperationName} found {State} live object {RelativePath}.")]
    public static partial void LogMirrorObjectPairState
    (
        this ILogger logger,
        string operationName,
        string relativePath,
        string state
    );

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "Mirror {OperationName} completed live object walk. New={NewCount}; Changed={ChangedCount}; Extra={ExtraCount}; Unchanged={UnchangedCount}.")]
    public static partial void LogMirrorObjectPairWalkCompleted
    (
        this ILogger logger,
        string operationName,
        int newCount,
        int changedCount,
        int extraCount,
        int unchangedCount
    );

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Warning,
        Message = "Mirror ignored duplicate {StoreRole} live object {RelativePath}.")]
    public static partial void LogMirrorDuplicateLiveObjectIgnored
    (
        this ILogger logger,
        string storeRole,
        string relativePath
    );
}

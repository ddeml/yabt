using Yabt.Common;

#pragma warning disable IDE0130 // Namespace does not match folder structure - Intentionally kept in the root namespace of the extended class for easier discoverability
namespace Microsoft.Extensions.Logging;
#pragma warning restore IDE0130 // Namespace does not match folder structure

internal static partial class HistoryDeduplicatorLogMessages
{
    [LoggerMessage(
        EventId = YabtEventIds.HistoryObjectRead,
        Level = LogLevel.Debug,
        Message = "Reading historical user-data object {RelativePath} for {Purpose}.")]
    public static partial void LogHistoryObjectRead(
        this ILogger logger,
        string relativePath,
        string purpose);

    [LoggerMessage(
        EventId = YabtEventIds.HistoryWouldDeduplicateObject,
        Level = LogLevel.Information,
        Message = "Would deduplicate historical user-data object {RelativePath} using materialized object {CanonicalPath}; estimated saving {BytesSaved} byte(s).")]
    public static partial void LogHistoryWouldDeduplicateObject(
        this ILogger logger,
        string relativePath,
        string canonicalPath,
        long bytesSaved);

    [LoggerMessage(
        EventId = YabtEventIds.HistoryObjectDeduplicated,
        Level = LogLevel.Information,
        Message = "Deduplicated historical user-data object {RelativePath} using materialized object {CanonicalPath}; saved {BytesSaved} byte(s).")]
    public static partial void LogHistoryObjectDeduplicated(
        this ILogger logger,
        string relativePath,
        string canonicalPath,
        long bytesSaved);

    [LoggerMessage(
        EventId = YabtEventIds.HistoryObjectUnchanged,
        Level = LogLevel.Debug,
        Message = "Historical user-data object {RelativePath} is unchanged: {Reason}.")]
    public static partial void LogHistoryObjectUnchanged(
        this ILogger logger,
        string relativePath,
        string reason);

    [LoggerMessage(
        EventId = YabtEventIds.HistoryControlMetadataOperation,
        Level = LogLevel.Debug,
        Message = "{Action} YABT history control metadata {Path}.")]
    public static partial void LogHistoryControlMetadataOperation(
        this ILogger logger,
        string action,
        string path);
}

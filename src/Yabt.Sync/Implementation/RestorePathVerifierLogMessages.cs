using Yabt.Common;

#pragma warning disable IDE0130 // Namespace does not match folder structure - Intentionally kept in the root namespace of the extended class for easier discoverability
namespace Microsoft.Extensions.Logging;
#pragma warning restore IDE0130 // Namespace does not match folder structure

internal static partial class RestorePathVerifierLogMessages
{
    [LoggerMessage(
        EventId = YabtEventIds.RestorePathVerificationRequested,
        Level = LogLevel.Information,
        Message = "Restore path verification requested from {SourceRoot} to {RestoreRoot}.")]
    public static partial void LogRestorePathVerificationRequested
    (
        this ILogger logger,
        string sourceRoot,
        string restoreRoot
    );

    [LoggerMessage(
        EventId = YabtEventIds.RestorePathVerificationCompleted,
        Level = LogLevel.Information,
        Message = "Restore path verification completed. Identical={Identical}; SourceOnly={SourceOnlyCount}; Different={DifferentCount}; RestoreOnly={RestoreOnlyCount}; Uncompared={UncomparedItemCount}; Files={ComparedFileCount}; Directories={ComparedDirectoryCount}.")]
    public static partial void LogRestorePathVerificationCompleted
    (
        this ILogger logger,
        bool identical,
        int sourceOnlyCount,
        int differentCount,
        int restoreOnlyCount,
        int uncomparedItemCount,
        int comparedFileCount,
        int comparedDirectoryCount
    );

    [LoggerMessage(
        EventId = YabtEventIds.RestorePathDifference,
        Level = LogLevel.Information,
        Message = "Restore path difference for {RelativePath}: {Difference}.")]
    public static partial void LogRestorePathDifference
    (
        this ILogger logger,
        string relativePath,
        string difference
    );

    [LoggerMessage(
        EventId = YabtEventIds.RestorePathItemUnchanged,
        Level = LogLevel.Debug,
        Message = "Restore path verification: {ItemKind} {RelativePath} is identical.")]
    public static partial void LogRestorePathItemUnchanged
    (
        this ILogger logger,
        string itemKind,
        string relativePath
    );
}

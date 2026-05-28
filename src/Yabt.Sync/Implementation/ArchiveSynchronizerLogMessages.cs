#pragma warning disable IDE0130 // Namespace does not match folder structure - Intentionally kept in the root namespace of the extended class for easier discoverability
namespace Microsoft.Extensions.Logging;
#pragma warning restore IDE0130 // Namespace does not match folder structure

internal static partial class ArchiveSynchronizerLogMessages
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Sync requested for {SourceRoot}. DryRun={DryRun}")]
    public static partial void LogSyncRequested(
        this ILogger logger,
        string sourceRoot,
        bool dryRun);
}

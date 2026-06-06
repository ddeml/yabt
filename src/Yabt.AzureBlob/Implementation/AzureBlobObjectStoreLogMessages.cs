using Yabt.Common;

#pragma warning disable IDE0130 // Namespace does not match folder structure - Intentionally kept in the root namespace of the extended class for easier discoverability
namespace Microsoft.Extensions.Logging;
#pragma warning restore IDE0130 // Namespace does not match folder structure

internal static partial class AzureBlobObjectStoreLogMessages
{
    [LoggerMessage(
        EventId = YabtEventIds.FallingBackToDownloadedAzureBlobMove,
        Level = LogLevel.Debug,
        Message = "Falling back to downloaded Azure Blob move from {SourceBlobName} to {DestinationBlobName}.")]
    public static partial void LogFallingBackToDownloadedAzureBlobMove
    (
        this ILogger logger,
        Exception exception,
        string sourceBlobName,
        string destinationBlobName
    );
}

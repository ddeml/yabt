using Yabt.Common;

#pragma warning disable IDE0130 // Namespace does not match folder structure - Intentionally kept in the root namespace of the extended class for easier discoverability
namespace Microsoft.Extensions.Logging;
#pragma warning restore IDE0130 // Namespace does not match folder structure

internal static partial class AzureBlobObjectStoreLogMessages
{
    [LoggerMessage(
        EventId = YabtEventIds.AzureBlobStoreReady,
        Level = LogLevel.Debug,
        Message = "Ensuring Azure Blob container {ContainerName} exists.")]
    public static partial void LogAzureBlobStoreReady
    (
        this ILogger logger,
        string containerName
    );

    [LoggerMessage(
        EventId = YabtEventIds.AzureBlobObjectUpload,
        Level = LogLevel.Debug,
        Message = "Uploading Azure Blob object {ObjectKey}.")]
    public static partial void LogAzureBlobObjectUpload
    (
        this ILogger logger,
        string objectKey
    );

    [LoggerMessage(
        EventId = YabtEventIds.AzureBlobObjectRead,
        Level = LogLevel.Debug,
        Message = "Opening Azure Blob object {ObjectKey} for reading.")]
    public static partial void LogAzureBlobObjectRead
    (
        this ILogger logger,
        string objectKey
    );

    [LoggerMessage(
        EventId = YabtEventIds.AzureBlobObjectConditionalReplace,
        Level = LogLevel.Debug,
        Message = "Attempting conditional replacement of Azure Blob object {ObjectKey}.")]
    public static partial void LogAzureBlobObjectConditionalReplace
    (
        this ILogger logger,
        string objectKey
    );

    [LoggerMessage(
        EventId = YabtEventIds.AzureBlobObjectConditionalDelete,
        Level = LogLevel.Debug,
        Message = "Attempting conditional deletion of Azure Blob object {ObjectKey}.")]
    public static partial void LogAzureBlobObjectConditionalDelete
    (
        this ILogger logger,
        string objectKey
    );

    [LoggerMessage(
        EventId = YabtEventIds.AzureBlobObjectExists,
        Level = LogLevel.Debug,
        Message = "Checking whether Azure Blob object {ObjectKey} exists.")]
    public static partial void LogAzureBlobObjectExists
    (
        this ILogger logger,
        string objectKey
    );

    [LoggerMessage(
        EventId = YabtEventIds.AzureBlobFolderList,
        Level = LogLevel.Debug,
        Message = "Listing Azure Blob folder prefix {FolderPrefix}. Recursive={Recursive}")]
    public static partial void LogAzureBlobFolderList
    (
        this ILogger logger,
        string folderPrefix,
        bool recursive
    );

    [LoggerMessage(
        EventId = YabtEventIds.AzureBlobObjectMove,
        Level = LogLevel.Debug,
        Message = "Moving Azure Blob object {SourceKey} to {DestinationKey}.")]
    public static partial void LogAzureBlobObjectMove
    (
        this ILogger logger,
        string sourceKey,
        string destinationKey
    );

    [LoggerMessage(
        EventId = YabtEventIds.AzureBlobFolderMove,
        Level = LogLevel.Debug,
        Message = "Moving Azure Blob folder prefix {SourcePrefix} to {DestinationPrefix}.")]
    public static partial void LogAzureBlobFolderMove
    (
        this ILogger logger,
        string sourcePrefix,
        string destinationPrefix
    );

    [LoggerMessage(
        EventId = YabtEventIds.AzureBlobConditionalMutationRead,
        Level = LogLevel.Debug,
        Message = "Reading Azure Blob object {ObjectKey} to validate a conditional mutation.")]
    public static partial void LogAzureBlobConditionalMutationRead
    (
        this ILogger logger,
        string objectKey
    );

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

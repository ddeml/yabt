using Yabt.Common;

#pragma warning disable IDE0130 // Namespace does not match folder structure - Intentionally kept in the root namespace of the extended class for easier discoverability
namespace Microsoft.Extensions.Logging;
#pragma warning restore IDE0130 // Namespace does not match folder structure

internal static partial class WebDavObjectStoreLogMessages
{
    [LoggerMessage(
        EventId = YabtEventIds.WebDavStoreReady,
        Level = LogLevel.Debug,
        Message = "Ensuring the configured WebDAV object-store root exists.")]
    public static partial void LogWebDavStoreReady(this ILogger logger);

    [LoggerMessage(
        EventId = YabtEventIds.WebDavObjectUpload,
        Level = LogLevel.Debug,
        Message = "Uploading WebDAV object {ObjectKey}.")]
    public static partial void LogWebDavObjectUpload
    (
        this ILogger logger,
        string objectKey
    );

    [LoggerMessage(
        EventId = YabtEventIds.WebDavObjectRead,
        Level = LogLevel.Debug,
        Message = "Opening WebDAV object {ObjectKey} for reading.")]
    public static partial void LogWebDavObjectRead
    (
        this ILogger logger,
        string objectKey
    );

    [LoggerMessage(
        EventId = YabtEventIds.WebDavObjectConditionalReplace,
        Level = LogLevel.Debug,
        Message = "Attempting conditional replacement of WebDAV object {ObjectKey}.")]
    public static partial void LogWebDavObjectConditionalReplace
    (
        this ILogger logger,
        string objectKey
    );

    [LoggerMessage(
        EventId = YabtEventIds.WebDavObjectConditionalDelete,
        Level = LogLevel.Debug,
        Message = "Attempting conditional deletion of WebDAV object {ObjectKey}.")]
    public static partial void LogWebDavObjectConditionalDelete
    (
        this ILogger logger,
        string objectKey
    );

    [LoggerMessage(
        EventId = YabtEventIds.WebDavObjectExists,
        Level = LogLevel.Debug,
        Message = "Checking whether WebDAV object {ObjectKey} exists.")]
    public static partial void LogWebDavObjectExists
    (
        this ILogger logger,
        string objectKey
    );

    [LoggerMessage(
        EventId = YabtEventIds.WebDavFolderList,
        Level = LogLevel.Debug,
        Message = "Listing WebDAV folder prefix {FolderPrefix}.")]
    public static partial void LogWebDavFolderList
    (
        this ILogger logger,
        string folderPrefix
    );

    [LoggerMessage(
        EventId = YabtEventIds.WebDavPathMove,
        Level = LogLevel.Debug,
        Message = "Moving WebDAV {PathKind} {SourcePath} to {DestinationPath}.")]
    public static partial void LogWebDavPathMove
    (
        this ILogger logger,
        string pathKind,
        string sourcePath,
        string destinationPath
    );

    [LoggerMessage(
        EventId = YabtEventIds.WebDavConditionalMutationRead,
        Level = LogLevel.Debug,
        Message = "Reading WebDAV object {ObjectKey} to validate a conditional mutation.")]
    public static partial void LogWebDavConditionalMutationRead
    (
        this ILogger logger,
        string objectKey
    );
}

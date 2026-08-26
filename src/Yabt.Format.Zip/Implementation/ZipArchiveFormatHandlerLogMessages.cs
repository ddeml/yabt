using Microsoft.Extensions.Logging;
using Yabt.Common;

namespace Yabt.Format.Zip.Implementation;

internal static partial class ZipArchiveFormatHandlerLogMessages
{
    [LoggerMessage(
        EventId = YabtEventIds.ZipPackageRead,
        Level = LogLevel.Debug,
        Message = "Opening staged ZIP package {PackagePath} for reading.")]
    public static partial void LogZipPackageRead
    (
        this ILogger logger,
        string packagePath
    );

    [LoggerMessage(
        EventId = YabtEventIds.ZipEntryRead,
        Level = LogLevel.Debug,
        Message = "Opening ZIP entry {EntryPath} from package {PackagePath} for reading.")]
    public static partial void LogZipEntryRead
    (
        this ILogger logger,
        string packagePath,
        string entryPath
    );

    [LoggerMessage(
        EventId = YabtEventIds.ZipSourceObjectRead,
        Level = LogLevel.Debug,
        Message = "Reading source object {SourceKey} into ZIP entry {EntryPath}.")]
    public static partial void LogZipSourceObjectRead
    (
        this ILogger logger,
        string sourceKey,
        string entryPath
    );

    [LoggerMessage(
        EventId = YabtEventIds.ZipRestoreArtifactRead,
        Level = LogLevel.Debug,
        Message = "Reading ZIP package artifact {ArtifactPath} for restore staging.")]
    public static partial void LogZipRestoreArtifactRead
    (
        this ILogger logger,
        string artifactPath
    );

    [LoggerMessage(
        EventId = YabtEventIds.ZipEmbeddedManifestRead,
        Level = LogLevel.Debug,
        Message = "Reading embedded manifest from ZIP package {PackagePath}.")]
    public static partial void LogZipEmbeddedManifestRead
    (
        this ILogger logger,
        string packagePath
    );

    [LoggerMessage(
        EventId = YabtEventIds.ZipAdjacentManifestRead,
        Level = LogLevel.Debug,
        Message = "Reading adjacent ZIP manifest artifact {ManifestPath}.")]
    public static partial void LogZipAdjacentManifestRead
    (
        this ILogger logger,
        string manifestPath
    );

    [LoggerMessage(
        EventId = YabtEventIds.ZipProjectedArtifactRead,
        Level = LogLevel.Debug,
        Message = "Opening generated ZIP {ArtifactRole} artifact {ArtifactPath} for reading.")]
    public static partial void LogZipProjectedArtifactRead
    (
        this ILogger logger,
        string artifactRole,
        string artifactPath
    );

    [LoggerMessage(
        EventId = YabtEventIds.ZipTemporaryPlumbingOperation,
        Level = LogLevel.Debug,
        Message = "{Action} ZIP temporary plumbing at {Path}.")]
    public static partial void LogZipTemporaryPlumbingOperation
    (
        this ILogger logger,
        string action,
        string path
    );

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

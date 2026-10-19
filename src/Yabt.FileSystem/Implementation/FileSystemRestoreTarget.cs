using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Yabt.Common.Async;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.FileSystem.Implementation;

internal sealed class FileSystemRestoreTarget
(
    string _rootPath,
    IObjectStore _store,
    ILogger _logger
) : IRestoreDestination
{
    private readonly string _fullRootPath = Path.GetFullPath(_rootPath);

    private readonly ConcurrentDictionary<string, FileSystemRestoreStagingFile> _stagingFiles = new(StringComparer.Ordinal);

    public string RootPath => _fullRootPath;

    public IObjectStore Store => _store;

    public IObjectStoreNamespace Namespace { get; } = new FileSystemObjectStoreNamespace(_rootPath);

    public bool DirectoryExists(ObjectPath path)
    {
        _logger.LogTrace(nameof(DirectoryExists));

        var nativePath = path.IsRoot ? _fullRootPath : ResolvePath(path.Value);
        EnsureNoReparsePointInExistingPath(nativePath);
        var attributes = FileSystemPathSafety.TryGetAttributes(nativePath);
        return attributes.HasValue && (attributes.Value & FileAttributes.Directory) != 0;
    }

    public Task EnsureSafeAsync
    (
        string? livePrefix,
        string? historyPrefix,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(EnsureSafeAsync));

        return YabtTask.Run
        (
            () =>
            {
                _logger.LogRestoreDestinationObjectRead(".", "restore destination root inspection");
                if (File.Exists(_fullRootPath))
                {
                    throw new YabtFileSystemException(
                        $"Restore destination '{_fullRootPath}' is a file, not a folder.");
                }

                if (!Directory.Exists(_fullRootPath)) { return; }

                var pendingDirectories = new Stack<string>();
                pendingDirectories.Push(_fullRootPath);
                while (pendingDirectories.TryPop(out var directoryPath))
                {
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        var directoryRelativePath = Path.GetRelativePath(
                            _fullRootPath,
                            directoryPath);
                        _logger.LogRestoreDestinationObjectRead(
                            ToArchiveRelativePath(directoryRelativePath),
                            "restore destination directory enumeration");
                    }
                    foreach (var itemPath in Directory.EnumerateFileSystemEntries(directoryPath))
                    {
                        // GetRelativePath may trim native Windows aliases, so validate the raw name first.
                        ValidateFileName(ToArchiveRelativePath(Path.GetFileName(itemPath)), itemPath);
                        var relativePath = ToArchiveRelativePath(
                            Path.GetRelativePath(_fullRootPath, itemPath));
                        ValidateRelativePath(relativePath);
                        _logger.LogRestoreDestinationObjectRead(
                            relativePath,
                            "restore destination attribute inspection");
                        var attributes = File.GetAttributes(itemPath);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new YabtFileSystemException(
                                $"Restore destination item '{relativePath}' is a reparse point " +
                                    "and cannot be historized safely.");
                        }

                        var isDirectory = (attributes & FileAttributes.Directory) != 0;
                        if (IsRequiredInternalDirectoryPath(
                                relativePath,
                                livePrefix,
                                historyPrefix) &&
                            !isDirectory)
                        {
                            throw new YabtFileSystemException(
                                $"Restore destination internal path '{relativePath}' must be a folder.");
                        }

                        if (isDirectory)
                        {
                            pendingDirectories.Push(itemPath);
                        }
                    }
                }
            },
            cancellationToken: cancellationToken
        );
    }

    public void ValidateRelativePath(string relativePath)
    {
        _logger.LogTrace(nameof(ValidateRelativePath));

        _ = ResolvePath(relativePath);
    }

    public Task CreateDirectoryAsync
    (
        string? relativePath,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(CreateDirectoryAsync));

        var path = string.IsNullOrEmpty(relativePath) ?
            _fullRootPath :
            ResolvePath(relativePath);
        return YabtTask.Run
        (
            () =>
            {
                EnsureNoReparsePointInExistingPath(path);
                Namespace.ValidatePath(ObjectPath.Parse(relativePath));
                Directory.CreateDirectory(path);
            },
            cancellationToken: cancellationToken
        );
    }

    public Task<IObjectStoreStagingArea> CreateStagingAreaAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogTrace(nameof(CreateStagingAreaAsync));

        var stagingRootPath = Path.Combine(
            _fullRootPath,
            ArchiveInternalFolderNames.TemporaryUploads,
            $"restore-{Guid.NewGuid():N}");
        return YabtTask.Run
        (
            () =>
            {
                EnsureStagingPathIsSafe(stagingRootPath);
                _logger.LogRestoreDestinationControlMetadataOperation(
                    "Creating restore staging directory",
                    stagingRootPath);
                Directory.CreateDirectory(stagingRootPath);
                EnsureStagingPathIsSafe(stagingRootPath);
                return (IObjectStoreStagingArea)new FileSystemRestoreStagingArea(this, stagingRootPath, _logger);
            },
            cancellationToken: cancellationToken
        );
    }

    public async Task CommitStagedFileAsync
    (
        string relativePath,
        string stagingToken,
        DateTimeOffset? lastModifiedUtc,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(CommitStagedFileAsync));

        var destinationPath = ResolvePath(relativePath);
        var destinationDirectory = Path.GetDirectoryName(destinationPath) ??
            throw new YabtFileSystemException(
                $"Restore path '{relativePath}' did not have a parent directory.");
        if (!_stagingFiles.TryGetValue(stagingToken, out var stagingFile))
        {
            throw new YabtFileSystemException("Restore staging token is not owned by this destination.");
        }
        var fullStagingPath = stagingFile.Path;
        var allowedStagingRoot = EnsureTrailingDirectorySeparator(Path.Combine(
            _fullRootPath,
            ArchiveInternalFolderNames.TemporaryUploads));
        if (!fullStagingPath.StartsWith(allowedStagingRoot, GetPathComparison()))
        {
            throw new YabtFileSystemException(
                $"Restore staging token '{stagingToken}' is outside the destination staging folder.");
        }

        try
        {
            await YabtTask.Run
            (
                () =>
                {
                    EnsureStagingPathIsSafe(fullStagingPath);
                    EnsureNoReparsePointInExistingPath(destinationPath);
                    Namespace.ValidatePath(ObjectPath.Parse(relativePath));
                    Directory.CreateDirectory(destinationDirectory);
                    EnsureNoReparsePointInExistingPath(destinationDirectory);
                    using
                    (
                        var stagingHandle = File.OpenHandle
                        (
                            fullStagingPath, FileMode.Open, FileAccess.Write, FileShare.Read
                        )
                    )
                    {
                        FileSystemFileIdentity.RequireIndependent(stagingHandle, fullStagingPath);
                        if (lastModifiedUtc.HasValue)
                        {
                            File.SetLastWriteTimeUtc(stagingHandle, lastModifiedUtc.Value.UtcDateTime);
                        }
                    }

                    _logger.LogRestoreDestinationControlMetadataOperation(
                        "Moving validated restore staging file into place",
                        fullStagingPath);
                    File.Move(fullStagingPath, destinationPath);
                    _stagingFiles.TryRemove(stagingToken, out _);
                    stagingFile.ReleaseToken(stagingToken);
                },
                cancellationToken: cancellationToken
            );
        }
        catch (Exception ex)
        {
            if (ex is YabtFileSystemException) { throw; }

            throw new YabtFileSystemException(
                $"Validated restore file '{relativePath}' could not be committed.",
                ex);
        }
    }

    private string ResolvePath(string relativePath)
    {
        _logger.LogTrace(nameof(ResolvePath));

        var normalizedPath = ArchiveLayout.NormalizeObjectKey(relativePath);
        if (string.IsNullOrEmpty(normalizedPath))
        {
            throw new YabtFileSystemException("Restore file path must not be empty.");
        }

        var segments = normalizedPath.Split('/');
        foreach (var segment in segments)
        {
            ValidateFileName(segment, relativePath);
        }

        var rootedPath = EnsureTrailingDirectorySeparator(_fullRootPath);
        var resolvedPath = Path.GetFullPath(
            Path.Combine(rootedPath, Path.Combine(segments)));
        if (!resolvedPath.StartsWith(rootedPath, GetPathComparison()))
        {
            throw new YabtFileSystemException(
                $"Restore path '{relativePath}' resolves outside the destination folder.");
        }

        EnsureNoReparsePointInExistingPath(resolvedPath);
        Namespace.ValidatePath(ObjectPath.Parse(normalizedPath));
        return resolvedPath;
    }

    private static void ValidateFileName(string name, string displayPath)
    {
        try
        {
            FileSystemObjectName.Validate(name);
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException($"Restore path '{displayPath}' is not valid on this filesystem.", ex);
        }
    }

    internal string RegisterStagingFile(string stagingPath, Action<string> releaseToken)
    {
        _logger.LogTrace(nameof(RegisterStagingFile));

        var token = Guid.NewGuid().ToString("N");
        _stagingFiles[token] = new(stagingPath, releaseToken);
        return token;
    }

    internal void ForgetStagingFile(string token)
    {
        _logger.LogTrace(nameof(ForgetStagingFile));

        _stagingFiles.TryRemove(token, out _);
    }

    internal void EnsureStagingPathIsSafe(string path)
    {
        _logger.LogTrace(nameof(EnsureStagingPathIsSafe));

        EnsureNoReparsePointInExistingPath(path);
        var stagingNamespace = new FileSystemObjectStoreNamespace(_fullRootPath, allowRootTemporaryDirectoryAlias: true);
        stagingNamespace.ValidatePath(ObjectPath.Parse(ToArchiveRelativePath(Path.GetRelativePath(_fullRootPath, path))));
    }

    private void EnsureNoReparsePointInExistingPath(string path)
    {
        _logger.LogTrace(nameof(EnsureNoReparsePointInExistingPath));

        var currentPath = path;
        while (currentPath.StartsWith(_fullRootPath, GetPathComparison()) &&
            !string.Equals(currentPath, _fullRootPath, GetPathComparison()))
        {
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogRestoreDestinationObjectRead(
                    ToArchiveRelativePath(Path.GetRelativePath(_fullRootPath, currentPath)),
                    "restore destination reparse-point inspection");
            }
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(currentPath);
            }
            catch (Exception exception)
            {
                if (exception is not FileNotFoundException && exception is not DirectoryNotFoundException)
                {
                    throw new YabtFileSystemException(
                        $"Restore path '{currentPath}' could not be inspected safely.",
                        exception);
                }

                currentPath = Path.GetDirectoryName(currentPath) ?? string.Empty;
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new YabtFileSystemException(
                    $"Restore path '{currentPath}' contains a reparse point.");
            }

            currentPath = Path.GetDirectoryName(currentPath) ?? string.Empty;
        }
    }

    private static string EnsureTrailingDirectorySeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ?
            path :
            $"{path}{Path.DirectorySeparatorChar}";

    private static bool IsRequiredInternalDirectoryPath
    (
        string relativePath,
        string? livePrefix,
        string? historyPrefix
    )
    {
        var normalizedPath = ArchiveLayout.NormalizeObjectKey(relativePath);
        var normalizedLivePrefix = ArchiveLayout.NormalizeObjectPrefix(livePrefix);
        var normalizedHistoryPrefix = ArchiveLayout.NormalizeObjectPrefix(historyPrefix);
        return normalizedLivePrefix is not null &&
                IsSameOrAncestor(normalizedPath, normalizedLivePrefix) ||
            normalizedHistoryPrefix is not null &&
                IsSameOrAncestor(normalizedPath, normalizedHistoryPrefix) ||
            IsSameOrAncestor(
                normalizedPath,
                ArchiveInternalFolderNames.TemporaryUploads);
    }

    private static bool IsSameOrAncestor(string candidatePath, string requiredDirectoryPath) =>
        string.Equals(
            candidatePath,
            requiredDirectoryPath,
            StringComparison.OrdinalIgnoreCase) ||
        requiredDirectoryPath.StartsWith(
            $"{candidatePath}/",
            StringComparison.OrdinalIgnoreCase);

    private static string ToArchiveRelativePath(string relativePath)
    {
        if (!OperatingSystem.IsWindows() && relativePath.Contains('\\'))
        {
            throw new YabtFileSystemException
            (
                $"Native restore destination path '{relativePath}' contains a literal backslash " +
                    "and cannot be represented as an archive path without changing its identity."
            );
        }

        return relativePath
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static StringComparison GetPathComparison() => FileSystemPathBackend.Current.NativePathComparison;
}

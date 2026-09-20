using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Yabt.Common.Async;
using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

internal sealed class FileSystemRestoreTarget
(
    string _rootPath,
    ILogger _logger
)
{
    private static readonly FrozenSet<string> WindowsReservedNames = new[]
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly string _fullRootPath = Path.GetFullPath(_rootPath);

    public string RootPath => _fullRootPath;

    public Task EnsureSafeAsync
    (
        string? livePrefix,
        string? historyPrefix,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(EnsureSafeAsync));

        return YabtTask.Run
        (
            () =>
            {
                _logger.LogObjectRead(".", "restore destination root inspection");
                if (File.Exists(_fullRootPath))
                {
                    throw new YabtSyncException(
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
                        _logger.LogObjectRead(
                            ToArchiveRelativePath(directoryRelativePath),
                            "restore destination directory enumeration");
                    }
                    foreach (var itemPath in Directory.EnumerateFileSystemEntries(directoryPath))
                    {
                        var relativePath = ToArchiveRelativePath(
                            Path.GetRelativePath(_fullRootPath, itemPath));
                        _logger.LogObjectRead(
                            relativePath,
                            "restore destination attribute inspection");
                        var attributes = File.GetAttributes(itemPath);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new YabtSyncException(
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
                            throw new YabtSyncException(
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
        CancellationToken cancellationToken
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
                Directory.CreateDirectory(path);
            },
            cancellationToken: cancellationToken
        );
    }

    public Task<string> CreateStagingDirectoryAsync(CancellationToken cancellationToken)
    {
        _logger.LogTrace(nameof(CreateStagingDirectoryAsync));

        var stagingRootPath = Path.Combine(
            _fullRootPath,
            ArchiveInternalFolderNames.TemporaryUploads,
            $"restore-{Guid.NewGuid():N}");
        return YabtTask.Run
        (
            () =>
            {
                EnsureNoReparsePointInExistingPath(_fullRootPath);
                _logger.LogControlMetadataOperation(
                    "Creating restore staging directory",
                    stagingRootPath);
                Directory.CreateDirectory(stagingRootPath);
                EnsureNoReparsePointInExistingPath(stagingRootPath);
                return stagingRootPath;
            },
            cancellationToken: cancellationToken
        );
    }

    public async Task CommitStagedFileAsync
    (
        string relativePath,
        string stagingPath,
        DateTimeOffset? lastModifiedUtc,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(CommitStagedFileAsync));

        var destinationPath = ResolvePath(relativePath);
        var destinationDirectory = Path.GetDirectoryName(destinationPath) ??
            throw new YabtSyncException(
                $"Restore path '{relativePath}' did not have a parent directory.");
        var fullStagingPath = Path.GetFullPath(stagingPath);
        var allowedStagingRoot = EnsureTrailingDirectorySeparator(Path.Combine(
            _fullRootPath,
            ArchiveInternalFolderNames.TemporaryUploads));
        if (!fullStagingPath.StartsWith(allowedStagingRoot, GetPathComparison()))
        {
            throw new YabtSyncException(
                $"Restore staging path '{stagingPath}' is outside the destination staging folder.");
        }

        try
        {
            await YabtTask.Run
            (
                () =>
                {
                    EnsureNoReparsePointInExistingPath(fullStagingPath);
                    EnsureNoReparsePointInExistingPath(destinationDirectory);
                    Directory.CreateDirectory(destinationDirectory);
                    EnsureNoReparsePointInExistingPath(destinationDirectory);
                    if (lastModifiedUtc.HasValue)
                    {
                        File.SetLastWriteTimeUtc(
                            fullStagingPath,
                            lastModifiedUtc.Value.UtcDateTime);
                    }

                    _logger.LogControlMetadataOperation(
                        "Moving validated restore staging file into place",
                        fullStagingPath);
                    File.Move(fullStagingPath, destinationPath);
                },
                cancellationToken: cancellationToken
            );
        }
        catch (Exception ex)
        {
            if (ex is YabtSyncException) { throw; }

            throw new YabtSyncException(
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
            throw new YabtSyncException("Restore file path must not be empty.");
        }

        var segments = normalizedPath.Split('/');
        var invalidCharacters = Path.GetInvalidFileNameChars();
        foreach (var segment in segments)
        {
            if (segment.IndexOfAny(invalidCharacters) >= 0 ||
                OperatingSystem.IsWindows() &&
                (segment.EndsWith(' ') ||
                    segment.EndsWith('.') ||
                    WindowsReservedNames.Contains(segment.Split('.')[0])))
            {
                throw new YabtSyncException(
                    $"Restore path '{relativePath}' is not valid on this filesystem.");
            }
        }

        var rootedPath = EnsureTrailingDirectorySeparator(_fullRootPath);
        var resolvedPath = Path.GetFullPath(
            Path.Combine(rootedPath, Path.Combine(segments)));
        if (!resolvedPath.StartsWith(rootedPath, GetPathComparison()))
        {
            throw new YabtSyncException(
                $"Restore path '{relativePath}' resolves outside the destination folder.");
        }

        return resolvedPath;
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
                _logger.LogObjectRead(
                    ToArchiveRelativePath(Path.GetRelativePath(_fullRootPath, currentPath)),
                    "restore destination reparse-point inspection");
            }
            if (Directory.Exists(currentPath) &&
                (File.GetAttributes(currentPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new YabtSyncException(
                    $"Restore path '{currentPath}' contains a directory reparse point.");
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

    private static string ToArchiveRelativePath(string relativePath) => relativePath
        .Replace(Path.DirectorySeparatorChar, '/')
        .Replace(Path.AltDirectorySeparatorChar, '/');

    private static StringComparison GetPathComparison() => OperatingSystem.IsWindows() ?
        StringComparison.OrdinalIgnoreCase :
        StringComparison.Ordinal;
}

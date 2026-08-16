using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yabt.Common.Async;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.FileSystem.Implementation;

internal sealed class FileSystemObjectStore
(
    IOptionsMonitor<FileSystemObjectStoreOptions> _options,
    ILogger<FileSystemObjectStore> _logger
) : IObjectStore
{
    private const int DefaultBufferSize = 81_920;
    private const int DefaultListChunkSize = 1_000;

    public Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogTrace(nameof(EnsureReadyAsync));

        var rootPath = GetRootPath();
        return YabtTask.Run
        (
            () =>
            {
                Directory.CreateDirectory(rootPath);
            },
            ex => _logger.LogAbandonedFileSystemOperationFailed
            (
                ex,
                nameof(Directory.CreateDirectory),
                rootPath
            ),
            cancellationToken);
    }

    public async Task UploadAsync
    (
        string key,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(UploadAsync));

        var normalizedKey = NormalizeObjectKey(key);
        var rootPath = GetRootPath();
        var destinationPath = GetObjectPath(rootPath, normalizedKey);
        var destinationDirectory = Path.GetDirectoryName(destinationPath) ??
            throw new YabtFileSystemException
            (
                "Filesystem object path did not include a directory.",
                normalizedKey,
                destinationPath
            );
        var temporaryDirectory = GetTemporaryDirectory(rootPath);
        var temporaryPath = Path.Combine
        (
            temporaryDirectory,
            $"{Guid.NewGuid():N}.tmp"
        );
        var bufferSize = GetEffectiveBufferSize();
        try
        {
            await using (var destination = await YabtTask.Run
            (
                () =>
                {
                    Directory.CreateDirectory(destinationDirectory);
                    Directory.CreateDirectory(temporaryDirectory);
                    return new FileStream
                    (
                        temporaryPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan
                    );
                },
                abandonedStream =>
                {
                    abandonedStream.Dispose();
                    TryDeleteFile(temporaryPath);
                },
                ex =>
                {
                    TryDeleteFile(temporaryPath);
                    _logger.LogAbandonedFileSystemOperationFailed
                    (
                        ex,
                        "Open temporary upload stream",
                        temporaryPath
                    );
                },
                cancellationToken
            ))
            {
                await content.CopyToAsync(destination, cancellationToken);
            }
            await YabtTask.Run
            (
                () => File.Move(temporaryPath, destinationPath),
                ex =>
                {
                    TryDeleteFile(temporaryPath);
                    _logger.LogAbandonedFileSystemOperationFailed
                    (
                        ex,
                        nameof(File.Move),
                        temporaryPath
                    );
                },
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException
            (
                $"Upload failed for filesystem object '{normalizedKey}'.",
                normalizedKey,
                destinationPath,
                ex
            );
        }
        finally
        {
            await YabtTask.Run(() => TryDeleteFile(temporaryPath), cancellationToken: default);
        }
    }

    public async Task<ArchiveObjectContent> OpenReadAsync
    (
        string key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(OpenReadAsync));

        var normalizedKey = NormalizeObjectKey(key);
        try
        {
            var path = GetObjectPath(GetRootPath(), normalizedKey);
            var bufferSize = GetEffectiveBufferSize();
            var stream = await YabtTask.Run
            (
                () => new FileStream
                (
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan
                ),
                abandonedStream => abandonedStream.Dispose(),
                ex => _logger.LogAbandonedFileSystemOperationFailed
                (
                    ex,
                    nameof(FileStream),
                    path
                ),
                cancellationToken
            );
            return new(stream);
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException(
                $"Open read failed for filesystem object '{normalizedKey}'.",
                normalizedKey,
                innerException: ex);
        }
    }

    public Task<bool> ExistsAsync
    (
        string key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ExistsAsync));
        var path = GetObjectPath(GetRootPath(), NormalizeObjectKey(key));
        return YabtTask.Run(() => File.Exists(path), cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<ArchiveFolderItem> GetFolderItemsAsync
    (
        string? folderPrefix,
        bool recursive = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(GetFolderItemsAsync));

        var chunkSize = GetEffectiveListChunkSize();
        var rootPath = GetRootPath();
        var normalizedPrefix = NormalizeObjectPrefix(folderPrefix);
        var listRootPath = string.IsNullOrEmpty(normalizedPrefix) ?
            rootPath :
            GetObjectPath(rootPath, normalizedPrefix);
        var items = new List<ArchiveFolderItem>(chunkSize);
        var enumerator = default(IEnumerator<ArchiveFolderItem>);
        void ReadChunk()
        {
            items.Clear();
            while(items.Count < chunkSize && enumerator!.MoveNext())
            {
                items.Add(enumerator.Current);
            }
        }
        try
        {
            await YabtTask.Run
            (
                () =>
                {
                    if (!Directory.Exists(listRootPath)) { return; }
                    var folderItems = EnumerateFolderItems(
                        rootPath,
                        listRootPath,
                        recursive);
                    enumerator = folderItems.GetEnumerator();
                    ReadChunk();
                },
                ex => _logger.LogAbandonedFileSystemOperationFailed
                (
                    ex,
                    "Initialize folder item list enumerator",
                    listRootPath
                ),
                cancellationToken
            );
            while(items.Count>0)
            {
                foreach (var item in items) { yield return item; }
                await YabtTask.Run
                (
                    ReadChunk,
                    ex => _logger.LogIgnoringAbandonedListChunkException(ex, listRootPath),
                    cancellationToken
                );
            }
        }
        finally
        {
            if (enumerator is not null)
            {
                _ = YabtTask.Run
                (
                    () =>
                    {
                        try
                        {
                            enumerator.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger.LogIgnoringListEnumeratorDisposeException(ex, listRootPath);
                        }
                    },
                    cancellationToken: default
                );
            }
        }
    }

    public Task MoveAsync
    (
        string source,
        string destination,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(MoveAsync));

        var normalizedSource = NormalizeObjectKey(source);
        var normalizedDestination = NormalizeObjectKey(destination);
        var rootPath = GetRootPath();
        var sourcePath = GetObjectPath(rootPath, normalizedSource);
        var destinationPath = GetObjectPath(rootPath, normalizedDestination);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new YabtFileSystemException(
                "Filesystem object path did not include a directory.",
                normalizedDestination,
                destinationPath);

        return YabtTask.Run
        (
            () =>
            {
                Directory.CreateDirectory(destinationDirectory);
                File.Move(sourcePath, destinationPath);
            },
            ex => _logger.LogAbandonedFileSystemOperationFailed
            (
                ex,
                nameof(File.Move),
                sourcePath
            ),
            cancellationToken
        );
    }

    public async Task MoveFolderAsync
    (
        string sourcePrefix,
        string destinationPrefix,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(MoveFolderAsync));

        var normalizedSourcePrefix = NormalizeObjectKey(sourcePrefix);
        var normalizedDestinationPrefix = NormalizeObjectKey(destinationPrefix);
        if (ArchiveLayout.IsUnderPrefix(normalizedDestinationPrefix, normalizedSourcePrefix))
        {
            throw new YabtFileSystemException(
                "Filesystem folder destination must not be the source folder or one of its descendants.",
                normalizedDestinationPrefix);
        }

        var rootPath = GetRootPath();
        var sourcePath = GetObjectPath(rootPath, normalizedSourcePrefix);
        var destinationPath = GetObjectPath(rootPath, normalizedDestinationPrefix);
        var destinationParent = Path.GetDirectoryName(destinationPath)
            ?? throw new YabtFileSystemException(
                "Filesystem folder path did not include a parent directory.",
                normalizedDestinationPrefix,
                destinationPath);

        try
        {
            await YabtTask.Run
            (
                () =>
                {
                    Directory.CreateDirectory(destinationParent);
                    Directory.Move(sourcePath, destinationPath);
                },
                ex => _logger.LogAbandonedFileSystemOperationFailed
                (
                    ex,
                    nameof(Directory.Move),
                    sourcePath
                ),
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException(
                $"Move failed for filesystem folder '{normalizedSourcePrefix}' to '{normalizedDestinationPrefix}'.",
                normalizedSourcePrefix,
                sourcePath,
                ex);
        }
    }

    private string GetRootPath()
    {
        var rootPath = _options.CurrentValue.RootPath;
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new YabtFileSystemException("Filesystem object store requires a root path.");
        }
        return Path.GetFullPath(rootPath);
    }

    private int GetEffectiveBufferSize()
    {
        var bufferSize = _options.CurrentValue.BufferSize ?? DefaultBufferSize;
        if (bufferSize <= 0)
        {
            throw new YabtFileSystemException("Filesystem object store buffer size must be greater than zero.");
        }
        return bufferSize;
    }

    private int GetEffectiveListChunkSize()
    {
        var chunkSize = _options.CurrentValue.ListChunkSize ?? DefaultListChunkSize;
        if (chunkSize <= 0)
        {
            throw new YabtFileSystemException("Filesystem object store list chunk size must be greater than zero.");
        }
        return chunkSize;
    }

    private static string GetObjectPath(string rootPath, string key)
    {
        try
        {
            return ResolveObjectPath(rootPath, key);
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException(
                $"Filesystem object path for '{key}' could not be resolved.",
                key,
                innerException: ex);
        }
    }

    private static string GetTemporaryDirectory(string rootPath) => ResolveObjectPath
    (
        rootPath,
        ArchiveInternalFolderNames.TemporaryUploads
    );

    private static IEnumerable<ArchiveFolderItem> EnumerateFolderItems
    (
        string rootPath,
        string directoryPath,
        bool recursive
    )
    {
        if (recursive)
        {
            return EnumerateRecursiveObjectItems(
                rootPath,
                directoryPath);
        }

        return EnumerateImmediateFolderItems(
            rootPath,
            directoryPath);
    }

    private static IEnumerable<ArchiveFolderItem> EnumerateImmediateFolderItems
    (
        string rootPath,
        string directoryPath
    )
    {
        var childDirectoryPaths = Directory.EnumerateDirectories(directoryPath).
            OrderBy(childDirectoryPath => childDirectoryPath, StringComparer.Ordinal);
        foreach (var childDirectoryPath in childDirectoryPaths)
        {
            var name = Path.GetFileName(childDirectoryPath);
            var key = ToArchiveRelativePath(Path.GetRelativePath(rootPath, childDirectoryPath));
            yield return ArchiveFolderItem.CreateFolder(name, key);
        }

        var filePaths = Directory.EnumerateFiles(directoryPath).
            OrderBy(filePath => filePath, StringComparer.Ordinal);
        foreach (var filePath in filePaths)
        {
            var name = Path.GetFileName(filePath);
            if (string.Equals(
                    name,
                    ArchiveFolderMarkerFileNames.EmptyFolder,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var info = new FileInfo(filePath);
            var key = ToArchiveRelativePath(Path.GetRelativePath(rootPath, filePath));
            yield return ArchiveFolderItem.CreateObject(
                name,
                new
                (
                    key,
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
                ));
        }
    }

    private static IEnumerable<ArchiveFolderItem> EnumerateRecursiveObjectItems
    (
        string rootPath,
        string directoryPath
    )
    {
        var childDirectoryPaths = Directory.EnumerateDirectories(directoryPath).
            OrderBy(childDirectoryPath => childDirectoryPath, StringComparer.Ordinal);
        foreach (var childDirectoryPath in childDirectoryPaths)
        {
            var childItems = EnumerateRecursiveObjectItems(
                rootPath,
                childDirectoryPath);

            foreach (var childItem in childItems)
            {
                yield return childItem;
            }
        }

        var fileItems = EnumerateFileItems(
            rootPath,
            directoryPath);
        foreach (var fileItem in fileItems)
        {
            yield return fileItem;
        }
    }

    private static IEnumerable<ArchiveFolderItem> EnumerateFileItems
    (
        string rootPath,
        string directoryPath
    )
    {
        var filePaths = Directory.EnumerateFiles(directoryPath).
            OrderBy(filePath => filePath, StringComparer.Ordinal);
        foreach (var filePath in filePaths)
        {
            var name = Path.GetFileName(filePath);
            if (string.Equals(
                    name,
                    ArchiveFolderMarkerFileNames.EmptyFolder,
                    StringComparison.Ordinal))
            {
                continue;
            }

            var info = new FileInfo(filePath);
            var key = ToArchiveRelativePath(Path.GetRelativePath(rootPath, filePath));
            yield return ArchiveFolderItem.CreateObject(
                name,
                new
                (
                    key,
                    info.Length,
                    new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
                ));
        }
    }

    private static string ResolveObjectPath(string rootPath, string objectPath)
    {
        var rootedPath = EnsureTrailingDirectorySeparator(Path.GetFullPath(rootPath));
        var relativePath = NormalizeRelativeObjectPath(objectPath);
        var fullPath = Path.GetFullPath(Path.Combine(rootedPath, relativePath));

        if (!fullPath.StartsWith(rootedPath, GetPathComparison()))
        {
            throw new YabtFileSystemException("Filesystem object path resolved outside the archive root.");
        }

        return fullPath;
    }

    private static string NormalizeRelativeObjectPath(string value)
    {
        var normalized = NormalizeObjectKey(value);
        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        var segments = normalized.Split
        (
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
        );

        foreach (var segment in segments)
        {
            if (segment.IndexOfAny(invalidFileNameChars) >= 0)
            {
                throw new YabtFileSystemException("Object path contains an invalid segment.");
            }
        }

        return Path.Combine(segments);
    }

    private static string NormalizeObjectKey(string? value)
    {
        var normalized = NormalizeObjectPrefix(value);
        if (string.IsNullOrEmpty(normalized))
        {
            throw new YabtFileSystemException("Object path must not be empty.");
        }
        return normalized;
    }

    private static string NormalizeObjectPrefix(string? value) => ArchiveLayout.NormalizeObjectKey(value);

    private static string ToArchiveRelativePath(string relativePath) => relativePath.
        Replace(Path.DirectorySeparatorChar, '/').
        Replace(Path.AltDirectorySeparatorChar, '/');

    private static string EnsureTrailingDirectorySeparator(string path) => Path.EndsInDirectorySeparator(path) ?
        path : $"{path}{Path.DirectorySeparatorChar}";

    private static StringComparison GetPathComparison() => OperatingSystem.IsWindows() ?
        StringComparison.OrdinalIgnoreCase :
        StringComparison.Ordinal;

    private void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) { File.Delete(path); }
        }
        catch (Exception ex)
        {
            _logger.LogIgnoringTemporaryObjectDeleteException(ex, path);
        }
    }

}

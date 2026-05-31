using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
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

    public Task EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogTrace(nameof(EnsureReadyAsync));

        cancellationToken.ThrowIfCancellationRequested();

        var rootPath = GetRootPath();
        Directory.CreateDirectory(rootPath);
        Directory.CreateDirectory(GetAreaPath(rootPath, ArchiveArea.Live));
        Directory.CreateDirectory(GetAreaPath(rootPath, ArchiveArea.Hist));

        return Task.CompletedTask;
    }

    public async Task UploadAsync
    (
        ArchiveObjectKey key,
        Stream content,
        string contentType,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(UploadAsync));

        _ = contentType;
        _ = metadata;

        cancellationToken.ThrowIfCancellationRequested();

        var rootPath = GetRootPath();
        var destinationPath = GetObjectPath(rootPath, key);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new YabtFileSystemException(
                "Filesystem object path did not include a directory.",
                key,
                destinationPath);

        Directory.CreateDirectory(destinationDirectory);

        var temporaryDirectory = GetTemporaryDirectory(rootPath);
        Directory.CreateDirectory(temporaryDirectory);

        var temporaryPath = Path.Combine(
            temporaryDirectory,
            $"{Guid.NewGuid():N}.tmp");

        try
        {
            var bufferSize = GetEffectiveBufferSize();
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await content.CopyToAsync(destination, cancellationToken);
            }

            File.Move(temporaryPath, destinationPath);
        }
        catch (Exception ex)
        {
            TryDeleteFile(temporaryPath);
            throw new YabtFileSystemException(
                $"Upload failed for filesystem object '{key.ToObjectPath()}'.",
                key,
                destinationPath,
                ex);
        }
    }

    public Task<ArchiveObjectContent> OpenReadAsync
    (
        ArchiveObjectKey key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(OpenReadAsync));

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var path = GetObjectPath(GetRootPath(), key);
            var bufferSize = GetEffectiveBufferSize();
            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            return Task.FromResult(new ArchiveObjectContent(stream));
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException(
                $"Open read failed for filesystem object '{key.ToObjectPath()}'.",
                key,
                innerException: ex);
        }
    }

    public Task<bool> ExistsAsync
    (
        ArchiveObjectKey key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ExistsAsync));

        cancellationToken.ThrowIfCancellationRequested();

        var path = GetObjectPath(GetRootPath(), key);
        return Task.FromResult(File.Exists(path));
    }

    public async IAsyncEnumerable<ArchiveObjectInfo> ListAsync
    (
        ArchiveArea area,
        string? prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ListAsync));

        var rootPath = GetRootPath();
        var areaPath = GetAreaPath(rootPath, area);
        var listRootPath = string.IsNullOrWhiteSpace(prefix) ?
            areaPath :
            GetObjectPath(rootPath, new(area, prefix));

        if (!Directory.Exists(listRootPath))
        {
            yield break;
        }

        await Task.Yield();

        foreach (var filePath in Directory.EnumerateFiles(
                     listRootPath,
                     "*",
                     SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = new FileInfo(filePath);
            var relativePath = ToArchiveRelativePath(Path.GetRelativePath(areaPath, filePath));

            yield return new(
                new(area, relativePath),
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
    }

    public Task MoveAsync
    (
        ArchiveObjectKey source,
        ArchiveObjectKey destination,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(MoveAsync));

        cancellationToken.ThrowIfCancellationRequested();

        var rootPath = GetRootPath();
        var sourcePath = GetObjectPath(rootPath, source);
        var destinationPath = GetObjectPath(rootPath, destination);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new YabtFileSystemException(
                "Filesystem object path did not include a directory.",
                destination,
                destinationPath);

        Directory.CreateDirectory(destinationDirectory);
        File.Move(sourcePath, destinationPath);

        return Task.CompletedTask;
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

    private static string GetAreaPath(string rootPath, ArchiveArea area)
    {
        return GetObjectPath(
            rootPath,
            new(area, string.Empty));
    }

    private static string GetObjectPath(string rootPath, ArchiveObjectKey key)
    {
        try
        {
            return ResolveObjectPath(rootPath, key.ToObjectPath());
        }
        catch (Exception ex)
        {
            throw new YabtFileSystemException(
                $"Filesystem object path for '{key.ToObjectPath()}' could not be resolved.",
                key,
                innerException: ex);
        }
    }

    private static string GetTemporaryDirectory(string rootPath)
    {
        return ResolveObjectPath(rootPath, ".yabt-tmp");
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
        var normalized = value.Replace('\\', '/').Trim('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new YabtFileSystemException("Object path must not be empty.");
        }

        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        var segments = normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            if (segment is "." or ".." || segment.IndexOfAny(invalidFileNameChars) >= 0)
            {
                throw new YabtFileSystemException("Object path contains an invalid segment.");
            }
        }

        return Path.Combine(segments);
    }

    private static string ToArchiveRelativePath(string relativePath)
    {
        return relativePath
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        return Path.EndsInDirectorySeparator(path) ?
            path :
            path + Path.DirectorySeparatorChar;
    }

    private static StringComparison GetPathComparison()
    {
        return OperatingSystem.IsWindows() ?
            StringComparison.OrdinalIgnoreCase :
            StringComparison.Ordinal;
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            _logger.LogIgnoringTemporaryObjectIoDeleteException(ex, path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogIgnoringTemporaryObjectAccessDeleteException(ex, path);
        }
    }
}

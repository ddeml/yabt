using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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

        Directory.CreateDirectory(GetRootPath());

        return Task.CompletedTask;
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

        _ = contentType;
        _ = metadata;

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedKey = NormalizeObjectKey(key);
        var rootPath = GetRootPath();
        var destinationPath = GetObjectPath(rootPath, normalizedKey);
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new YabtFileSystemException(
                "Filesystem object path did not include a directory.",
                normalizedKey,
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
            await using (var destination = new FileStream
            (
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            ))
            {
                await content.CopyToAsync(destination, cancellationToken);
            }

            File.Move(temporaryPath, destinationPath);
        }
        catch (Exception ex)
        {
            TryDeleteFile(temporaryPath);
            throw new YabtFileSystemException(
                $"Upload failed for filesystem object '{normalizedKey}'.",
                normalizedKey,
                destinationPath,
                ex);
        }
    }

    public Task<ArchiveObjectContent> OpenReadAsync
    (
        string key,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(OpenReadAsync));

        cancellationToken.ThrowIfCancellationRequested();

        var normalizedKey = NormalizeObjectKey(key);
        try
        {
            var path = GetObjectPath(GetRootPath(), normalizedKey);
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

        cancellationToken.ThrowIfCancellationRequested();

        var path = GetObjectPath(GetRootPath(), NormalizeObjectKey(key));
        return Task.FromResult(File.Exists(path));
    }

    public async IAsyncEnumerable<ArchiveObjectInfo> ListAsync
    (
        string? prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ListAsync));

        var rootPath = GetRootPath();
        var normalizedPrefix = NormalizeObjectPrefix(prefix);
        var listRootPath = string.IsNullOrEmpty(normalizedPrefix) ?
            rootPath :
            GetObjectPath(rootPath, normalizedPrefix);

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
            var key = ToArchiveRelativePath(Path.GetRelativePath(rootPath, filePath));

            yield return new
            (
                key,
                info.Length,
                new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
            );
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

        cancellationToken.ThrowIfCancellationRequested();

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

    private static string NormalizeObjectPrefix(string? value)
    {
        return ArchiveLayout.NormalizeObjectKey(value);
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

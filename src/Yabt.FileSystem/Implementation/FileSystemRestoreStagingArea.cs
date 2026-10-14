using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;

namespace Yabt.FileSystem.Implementation;

internal sealed class FileSystemRestoreStagingArea
(
    FileSystemRestoreTarget _destination,
    string _rootPath,
    ILogger _logger
) : IObjectStoreStagingArea
{
    private const int DefaultBufferSize = 81_920;
    private readonly ConcurrentDictionary<string, string> _files = new(StringComparer.Ordinal);
    private bool _disposed;

    public string CreateFileToken()
    {
        _logger.LogTrace(nameof(CreateFileToken));

        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = Path.Combine(_rootPath, $"{Guid.NewGuid():N}.tmp");
        var token = _destination.RegisterStagingFile(path, ForgetCommittedToken);
        _files[token] = path;
        return token;
    }

    public Stream OpenWrite(string stagingToken)
    {
        _logger.LogTrace(nameof(OpenWrite));

        ObjectDisposedException.ThrowIf(_disposed, this);
        var path = ResolveToken(stagingToken);
        _destination.EnsureStagingPathIsSafe(path);
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = DefaultBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(path, options);
    }

    public void DeleteFile(string stagingToken)
    {
        _logger.LogTrace(nameof(DeleteFile));

        if (!_files.TryGetValue(stagingToken, out var path)) { return; }
        try
        {
            _destination.EnsureStagingPathIsSafe(path);
            File.Delete(path);
            _files.TryRemove(stagingToken, out _);
            _destination.ForgetStagingFile(stagingToken);
        }
        catch (Exception ex)
        {
            _logger.LogRestoreDestinationTemporaryCleanupFailed(ex, path);
        }
    }

    public ValueTask DisposeAsync()
    {
        _logger.LogTrace(nameof(DisposeAsync));

        if (_disposed) { return ValueTask.CompletedTask; }
        _disposed = true;
        foreach (var token in _files.Keys)
        {
            DeleteFile(token);
            _destination.ForgetStagingFile(token);
        }

        try
        {
            _destination.EnsureStagingPathIsSafe(_rootPath);
            Directory.Delete(_rootPath, recursive: false);
        }
        catch (Exception ex)
        {
            _logger.LogRestoreDestinationTemporaryCleanupFailed(ex, _rootPath);
        }

        return ValueTask.CompletedTask;
    }

    private string ResolveToken(string token)
    {
        _logger.LogTrace(nameof(ResolveToken));

        return _files.TryGetValue(token, out var path) ? path : throw new YabtFileSystemException
        (
            "Restore staging token does not belong to this staging area."
        );
    }

    private void ForgetCommittedToken(string token)
    {
        _logger.LogTrace(nameof(ForgetCommittedToken));

        _files.TryRemove(token, out _);
    }
}

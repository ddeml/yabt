using System.Text;
using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;

namespace Yabt.FileSystem.Implementation;

internal sealed class FileSystemArchiveMutationLock
(
    FileStream _lockStream,
    string _lockPath,
    ILogger<FileSystemObjectStore> _logger
) : IArchiveMutationLock
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);

    public CancellationToken LockLostToken => default;

    public static async Task<IArchiveMutationLock> AcquireAsync
    (
        string lockPath,
        ILogger<FileSystemObjectStore> logger,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        ArgumentNullException.ThrowIfNull(logger);
        logger.LogTrace(nameof(AcquireAsync));

        var lockDirectory = Path.GetDirectoryName(lockPath) ??
            throw new YabtFileSystemException(
                "Filesystem archive mutation lock did not include a parent directory.",
                path: lockPath);
        Directory.CreateDirectory(lockDirectory);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                logger.LogFileSystemPlumbingOperation(
                    "Creating or opening archive-mutation lock file",
                    lockPath);
                var lockStream = new FileStream
                (
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose
                );
                try
                {
                    var lockContent = Encoding.UTF8.GetBytes
                    (
                        "{\"documentType\":\"yabt.archiveMutationLock\",\"schemaVersion\":1}\n"
                    );
                    lockStream.SetLength(0);
                    await lockStream.WriteAsync(lockContent, cancellationToken);
                    await lockStream.FlushAsync(cancellationToken);
                    lockStream.Position = 0;
                    logger.LogFileSystemPlumbingOperation(
                        "Wrote and acquired archive-mutation lock file",
                        lockPath);
                    return new FileSystemArchiveMutationLock(
                        lockStream,
                        lockPath,
                        logger);
                }
                catch
                {
                    await lockStream.DisposeAsync();
                    throw;
                }
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                logger.LogFileSystemPlumbingOperation(
                    "Found busy archive-mutation lock file",
                    lockPath);
                await Task.Delay(RetryInterval, cancellationToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogTrace(nameof(DisposeAsync));
        _logger.LogFileSystemPlumbingOperation(
            "Releasing and deleting archive-mutation lock file",
            _lockPath);
        await _lockStream.DisposeAsync();
    }
}

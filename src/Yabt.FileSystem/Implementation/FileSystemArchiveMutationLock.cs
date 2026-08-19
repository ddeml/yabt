using System.Text;
using Yabt.Core.Abstractions;

namespace Yabt.FileSystem.Implementation;

internal sealed class FileSystemArchiveMutationLock(FileStream _lockStream) : IArchiveMutationLock
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(250);

    public CancellationToken LockLostToken => default;

    public static async Task<IArchiveMutationLock> AcquireAsync
    (
        string lockPath,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);

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
                    return new FileSystemArchiveMutationLock(lockStream);
                }
                catch
                {
                    await lockStream.DisposeAsync();
                    throw;
                }
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                await Task.Delay(RetryInterval, cancellationToken);
            }
        }
    }

    public ValueTask DisposeAsync() => _lockStream.DisposeAsync();
}

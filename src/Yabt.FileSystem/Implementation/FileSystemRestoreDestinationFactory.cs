using Microsoft.Extensions.Logging;
using Yabt.Common;
using Yabt.Core.Abstractions;

namespace Yabt.FileSystem.Implementation;

internal sealed class FileSystemRestoreDestinationFactory
(
    IEnumerable<ISourceRootObjectStoreResolver> _storeResolvers,
    ILogger<FileSystemRestoreTarget> _destinationLogger
) : IRestoreDestinationFactory
{
    private const int DefaultBufferSize = 81_920;

    public IRestoreDestination Create(string rootPath, IObjectStore? store = null)
    {
        _destinationLogger.LogTrace(nameof(Create));

        FileSystemNativePath.ValidateSpelling(rootPath);
        var fullRootPath = Path.GetFullPath(rootPath);
        return new FileSystemRestoreTarget
        (
            fullRootPath,
            store ?? _storeResolvers.Single().ResolveSourceRoot(fullRootPath),
            _destinationLogger
        );
    }

    public Stream CreateComparisonReplayStream()
    {
        _destinationLogger.LogTrace(nameof(CreateComparisonReplayStream));

        // A private replay is independent of archive staging and is removed when its owner closes it.
        var temporaryDirectory = Path.GetTempPath();
        TemporaryStorageValidation.ValidateDirectory(temporaryDirectory);
        var temporaryPath = Path.Combine(temporaryDirectory, $"yabt-source-replay-{Guid.NewGuid():N}.tmp");
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = DefaultBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        return new FileStream(temporaryPath, options);
    }
}

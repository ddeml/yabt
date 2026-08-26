using Microsoft.Extensions.Logging;
using Yabt.Core.Models;

namespace Yabt.Format.Zip.Implementation;

internal sealed class StagedZipRestorePackage
(
    string _path,
    string _artifactRelativePath,
    ILogger _logger
) : IAsyncDisposable
{
    private bool _disposed;

    public FileStream OpenPackage()
    {
        _logger.LogTrace(nameof(OpenPackage));
        _logger.LogZipPackageRead(_artifactRelativePath);

        ObjectDisposedException.ThrowIf(_disposed, this);
        return new
        (
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );
    }

    public Task<ArchiveObjectContent> OpenEntryAsync
    (
        string entryFullName,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(OpenEntryAsync));
        _logger.LogZipEntryRead(_artifactRelativePath, entryFullName);

        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        FileStream? packageContent = null;
        System.IO.Compression.ZipArchive? archive = null;
        Stream? entryContent = null;
        try
        {
            packageContent = OpenPackage();
            archive = new
            (
                packageContent,
                System.IO.Compression.ZipArchiveMode.Read,
                leaveOpen: true
            );
            var entry = archive.GetEntry(entryFullName) ??
                throw new YabtFormatZipException(
                    $"Staged ZIP package '{_artifactRelativePath}' no longer contains " +
                        $"entry '{entryFullName}'.");
            entryContent = entry.Open();
            var ownedContent = new ZipArchiveEntryReadStream(
                entryContent,
                archive,
                packageContent);
            entryContent = null;
            archive = null;
            packageContent = null;
            return Task.FromResult(new ArchiveObjectContent(ownedContent));
        }
        catch (Exception ex)
        {
            try
            {
                entryContent?.Dispose();
            }
            finally
            {
                try
                {
                    archive?.Dispose();
                }
                finally
                {
                    packageContent?.Dispose();
                }
            }

            if (ex is YabtFormatZipException) { throw; }

            throw new YabtFormatZipException(
                $"ZIP entry '{entryFullName}' from package '{_artifactRelativePath}' " +
                    "could not be opened for restore.",
                ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        _logger.LogTrace(nameof(DisposeAsync));

        if (_disposed) { return ValueTask.CompletedTask; }

        _disposed = true;
        _logger.LogZipTemporaryPlumbingOperation(
            "Deleting restore staging file",
            _path);
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex)
        {
            _logger.LogIgnoringZipRestoreTemporaryPathDeleteException(ex, _path);
        }

        return ValueTask.CompletedTask;
    }
}

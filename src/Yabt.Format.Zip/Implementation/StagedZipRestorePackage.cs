using Microsoft.Extensions.Logging;
using Yabt.Common;
using Yabt.Core.Models;

namespace Yabt.Format.Zip.Implementation;

internal sealed class StagedZipRestorePackage
(
    TemporaryContent _content,
    TemporaryIndex _index,
    DiskDictionary<ZipEntryRecord> entries,
    string _artifactRelativePath,
    ILogger _logger
) : IAsyncDisposable
{
    private bool _disposed;

    public DiskDictionary<ZipEntryRecord> Entries { get; } = entries;

    public Stream OpenPackage()
    {
        _logger.LogTrace(nameof(OpenPackage));
        _logger.LogZipPackageRead(_artifactRelativePath);
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _content.OpenRead();
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
        try
        {
            if (!Entries.TryGetValue(entryFullName, out var entry))
            {
                throw new YabtFormatZipException(
                    $"Staged ZIP package '{_artifactRelativePath}' no longer contains entry '{entryFullName}'.");
            }

            return Task.FromResult(new ArchiveObjectContent(IndexedZipReader.OpenEntry(OpenPackage(), entry)));
        }
        catch (Exception ex)
        {
            if (ex is YabtFormatZipException) { throw; }
            throw new YabtFormatZipException(
                $"ZIP entry '{entryFullName}' from package '{_artifactRelativePath}' could not be opened for restore.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogTrace(nameof(DisposeAsync));
        if (_disposed) { return; }
        _disposed = true;
        _logger.LogZipTemporaryPlumbingOperation("Disposing restore package staging", _artifactRelativePath);
        try { Entries.Dispose(); }
        finally
        {
            try { _index.Dispose(); }
            finally { await _content.DisposeAsync(); }
        }
    }
}

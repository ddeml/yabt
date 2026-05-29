using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Format.Zip.Implementation;

internal sealed class ZipArchiveFormatProvider(ILogger<ZipArchiveFormatProvider> _logger) : IArchiveFormatProvider
{
    public string FormatName => ZipArchiveFormatName.Value;

    public Task<ArchiveFormatOperationResult> BackupAsync
    (
        ArchiveFormatBackupRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(BackupAsync));

        throw new NotImplementedException("Zip format provider is scaffolded but not implemented yet.");
    }

    public Task<ArchiveFormatOperationResult> RestoreAsync
    (
        ArchiveFormatRestoreRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(RestoreAsync));

        throw new NotImplementedException("Zip format provider is scaffolded but not implemented yet.");
    }

    public Task<ArchiveFormatOperationResult> VerifyAsync
    (
        ArchiveFormatVerifyRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(VerifyAsync));

        throw new NotImplementedException("Zip format provider is scaffolded but not implemented yet.");
    }
}

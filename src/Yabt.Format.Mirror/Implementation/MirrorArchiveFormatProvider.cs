using Microsoft.Extensions.Logging;
using Yabt.Core.Abstractions;
using Yabt.Core.Models;

namespace Yabt.Format.Mirror.Implementation;

internal sealed class MirrorArchiveFormatProvider(ILogger<MirrorArchiveFormatProvider> _logger) : IArchiveFormatProvider
{
    public string FormatName => MirrorArchiveFormatName.Value;

    public Task<ArchiveFormatOperationResult> BackupAsync
    (
        ArchiveFormatBackupRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(BackupAsync));

        throw new NotImplementedException("Mirror format provider is scaffolded but not implemented yet.");
    }

    public Task<ArchiveFormatOperationResult> RestoreAsync
    (
        ArchiveFormatRestoreRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(RestoreAsync));

        throw new NotImplementedException("Mirror format provider is scaffolded but not implemented yet.");
    }

    public Task<ArchiveFormatOperationResult> VerifyAsync
    (
        ArchiveFormatVerifyRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(VerifyAsync));

        throw new NotImplementedException("Mirror format provider is scaffolded but not implemented yet.");
    }
}

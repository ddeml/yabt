using Yabt.Core.Models;

namespace Yabt.Core.Abstractions;

public interface IArchiveFormatProvider
{
    string FormatName { get; }

    Task<ArchiveFormatOperationResult> BackupAsync
    (
        ArchiveFormatBackupRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ArchiveFormatOperationResult> RestoreAsync
    (
        ArchiveFormatRestoreRequest request,
        CancellationToken cancellationToken = default
    );

    Task<ArchiveFormatOperationResult> VerifyAsync
    (
        ArchiveFormatVerifyRequest request,
        CancellationToken cancellationToken = default
    );
}

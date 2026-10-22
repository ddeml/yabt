using Microsoft.Extensions.Logging;
using Yabt.Common;

namespace Yabt.Sync.Implementation;

internal sealed class ArchiveSynchronizer
(
    ILogger<ArchiveSynchronizer> _logger,
    ArchiveBackupOperation _backup,
    ArchiveRestoreOperation _restore
) : IArchiveSynchronizer
{
    public Task<SyncRunResult> BackupAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(BackupAsync));

        return RunArchiveOperationAsync(
            request,
            ArchiveOperationDirection.Backup,
            cancellationToken);
    }

    public Task<SyncRunResult> SyncAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(SyncAsync));

        return BackupAsync(request, cancellationToken);
    }

    public Task<SyncRunResult> RestoreAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(RestoreAsync));

        return RunArchiveOperationAsync(
            request,
            ArchiveOperationDirection.Restore,
            cancellationToken);
    }

    private async Task<SyncRunResult> RunArchiveOperationAsync
    (
        SyncRunRequest request,
        ArchiveOperationDirection direction,
        CancellationToken cancellationToken
    )
    {
        _logger.LogTrace(nameof(RunArchiveOperationAsync));

        using var configurationWarnings = ConfigurationWarnings.BeginOperation();
        ArgumentNullException.ThrowIfNull(request);
        return await (direction switch
        {
            ArchiveOperationDirection.Backup => _backup.RunBackupOperationAsync(
                request,
                cancellationToken),
            ArchiveOperationDirection.Restore => _restore.RunRestoreOperationAsync(
                request,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, null),
        });
    }

    public Task<SyncRunResult> ScanAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ScanAsync));

        _ = request;
        cancellationToken.ThrowIfCancellationRequested();

        //TODO: Add scalable change detection abstractions before implementing real scan output.
        return Task.FromResult(new SyncRunResult
        (
            Completed: true,
            Message: "Scan completed as a no-op placeholder; change detection is not implemented yet."
        ));
    }

    public Task<SyncRunResult> PackAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(PackAsync));

        _ = request;
        cancellationToken.ThrowIfCancellationRequested();

        //TODO: Decide whether pack writes package artifacts locally, to a target store, or only previews projection output.
        return Task.FromResult(new SyncRunResult
        (
            Completed: false,
            Message: "Pack command wiring is implemented, but standalone pack semantics are still intentionally open."
        ));
    }

    public Task<SyncRunResult> ReconcileAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(ReconcileAsync));

        _ = request;
        cancellationToken.ThrowIfCancellationRequested();

        //TODO: Define bidirectional reconciliation conflict handling before mutating either side.
        return Task.FromResult(new SyncRunResult
        (
            Completed: false,
            Message: "Reconcile command wiring is implemented, but conflict semantics are still intentionally open."
        ));
    }

    public Task<SyncRunResult> VerifyAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogTrace(nameof(VerifyAsync));

        return _backup.VerifyAsync(request, cancellationToken);
    }

}

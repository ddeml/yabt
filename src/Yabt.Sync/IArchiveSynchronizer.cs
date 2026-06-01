namespace Yabt.Sync;

public interface IArchiveSynchronizer
{
    Task<SyncRunResult> SyncAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    );

    Task<SyncRunResult> RestoreAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    );

    Task<SyncRunResult> ScanAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    );

    Task<SyncRunResult> VerifyAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    );

    Task<SyncRunResult> PackAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    );

    Task<SyncRunResult> ReconcileAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    );
}

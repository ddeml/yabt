namespace Yabt.Sync;

public interface IArchiveSynchronizer
{
    Task<SyncRunResult> SyncAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    );
}

namespace Yabt.Sync;

public interface IHistoryDeduplicator
{
    Task<HistoryDeduplicationResult> DeduplicateAsync
    (
        HistoryDeduplicationRequest request,
        CancellationToken cancellationToken = default
    );
}

using Microsoft.Extensions.Logging;

namespace Yabt.Sync;

public sealed class ArchiveSynchronizer(ILogger<ArchiveSynchronizer> _logger) : IArchiveSynchronizer
{
    public Task<SyncRunResult> SyncAsync
    (
        SyncRunRequest request,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation(
            "Sync requested for {SourceRoot}. DryRun={DryRun}",
            request.SourceRoot,
            request.DryRun);

        return Task.FromResult(new SyncRunResult(
            Completed: false,
            Message: "Synchronization planning is scaffolded but not implemented yet."));
    }
}

using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Yabt.Common;
using Yabt.Core.Abstractions;

namespace Yabt.Sync.Implementation;

internal sealed class OperationProgressFactory
(
    IOptionsMonitor<YabtSyncOptions> _options,
    TimeProvider _timeProvider,
    ILogger<OperationProgressFactory> _logger
)
{
    public OperationProgress? Open
    (
        string direction,
        IReadOnlyObjectStore source,
        IReadOnlyObjectStore target,
        IEnumerable<string?> identityParts
    )
    {
        _logger.LogTrace(nameof(Open));
        var options = _options.CurrentValue;
        if (options.ProgressEnabled == false ||
            source is not IObjectStoreOperationIdentity sourceIdentity ||
            target is not IObjectStoreOperationIdentity targetIdentity)
        {
            return null;
        }

        var batchSize = options.GetValidatedProgressFlushUnitCount();
        var interval = options.GetValidatedProgressFlushInterval();
        string directory;
        try { directory = options.GetEffectiveProgressDirectory(); }
        catch (Exception ex)
        {
            if (options.ProgressDirectory is not null) { throw; }
            _logger.LogOperationProgressUnavailable(ex);
            return null;
        }
        // A configured location inside operation data is an error, not a cache
        // failure. Keep provider-owned overlap validation outside the fallback.
        TemporaryStorageValidation.ValidateDirectory(directory);
        try
        {
            LocalStateDirectory.CreatePrivateDirectory(directory);
        }
        catch (Exception ex)
        {
            _logger.LogOperationProgressUnavailable(ex);
            return null;
        }
        TemporaryStorageValidation.ValidateDirectory(directory);
        try
        {
            var identity = JsonSerializer.Serialize(new
            {
                Version = 1,
                Direction = direction,
                Source = sourceIdentity.OperationIdentity,
                Target = targetIdentity.OperationIdentity,
                Settings = identityParts,
            });
            var journal = OperationProgressJournal.Open(directory, identity, _timeProvider, batchSize, interval);
            if (journal.IsResuming) { _logger.LogOperationProgressResumed(); }
            return new(journal, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogOperationProgressUnavailable(ex);
            return null;
        }
    }
}

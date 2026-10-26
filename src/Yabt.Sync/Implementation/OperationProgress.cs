using Microsoft.Extensions.Logging;

namespace Yabt.Sync.Implementation;

/// <summary>
/// Failure-isolated scheduling hints. No record authorizes skipping a read,
/// publishing evidence, or replaying a mutation.
/// </summary>
internal sealed class OperationProgress
(
    OperationProgressJournal journal,
    ILogger _logger
) : IDisposable
{
    private OperationProgressJournal? _journal = journal;

    public bool HasUnfinishedSubtree(string path)
    {
        _logger.LogTrace(nameof(HasUnfinishedSubtree));
        if (_journal is null) { return true; }
        try { return _journal.HasUnfinishedSubtree(path); }
        catch (Exception ex) { Disable(ex); return true; }
    }

    public void StartUnit(string path)
    {
        _logger.LogTrace(nameof(StartUnit));
        try { _journal?.StartUnit(path); }
        catch (Exception ex) { Disable(ex); }
    }

    public void CompleteUnit(string path, IEnumerable<string> children)
    {
        _logger.LogTrace(nameof(CompleteUnit));
        try { _journal?.CompleteUnit(path, children); }
        catch (Exception ex) { Disable(ex); }
    }

    public void CompleteOperation()
    {
        _logger.LogTrace(nameof(CompleteOperation));
        try { _journal?.CompleteOperation(); }
        catch (Exception ex) { Disable(ex); }
    }

    private void Disable(Exception exception)
    {
        _logger.LogTrace(nameof(Disable));
        _logger.LogOperationProgressUnavailable(exception);
        Dispose();
    }

    public void Dispose()
    {
        _logger.LogTrace(nameof(Dispose));
        var owned = _journal;
        _journal = null;
        try { owned?.Dispose(); }
        catch (Exception ex) { _logger.LogOperationProgressCleanupFailed(ex); }
    }
}

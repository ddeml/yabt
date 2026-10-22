namespace Yabt.Core.Models;

/// <summary>Owns resources acquired while a projection is enumerated or materialized.</summary>
public sealed class ArchiveProjectionLifetime : IAsyncDisposable
{
    private readonly List<IAsyncDisposable> _resources = [];
    private bool _disposed;

    public void Add(IAsyncDisposable resource)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _resources.Add(resource);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _disposed = true;
        List<Exception>? failures = null;
        for (var index = _resources.Count - 1; index >= 0; index--)
        {
            try { await _resources[index].DisposeAsync(); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
        }
        _resources.Clear();
        if (failures is not null) { throw new AggregateException(failures); }
    }
}

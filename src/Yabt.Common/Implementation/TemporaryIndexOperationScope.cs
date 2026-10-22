namespace Yabt.Common.Implementation;

internal sealed class TemporaryIndexOperationScope
(
    TemporaryContentOptions options,
    TemporaryIndexOperationScope? _previous
) : IDisposable
{
    private SqliteTemporaryIndex? _index;
    private readonly Lock _gate = new();
    private bool _disposed;

    public TemporaryIndex CreateLease()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _index ??= new(options);
            _index.AddReference();
            return new TemporaryIndexLease(_index);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            if (ReferenceEquals(TemporaryIndex.ActiveOperation.Value, this))
            {
                TemporaryIndex.ActiveOperation.Value = _previous;
            }

            _index?.ReleaseReference();
        }
    }
}

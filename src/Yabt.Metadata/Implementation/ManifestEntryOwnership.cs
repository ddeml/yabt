namespace Yabt.Metadata.Implementation;

internal sealed class ManifestEntryOwnership(object entries, object original) : IDisposable
{
    private IDisposable? _owned = ReferenceEquals(entries, original) ? null : entries as IDisposable;

    public T Complete<T>(T result)
    {
        _owned = null;
        return result;
    }

    public void Dispose() => Interlocked.Exchange(ref _owned, null)?.Dispose();
}

namespace Yabt.Core.Models;

/// <summary>Owns projected content until every artifact consumer has finished.</summary>
public sealed class ArchiveBackupProjection
(
    IAsyncEnumerable<ArchiveProjectedObject> _objects,
    IAsyncDisposable? lifetime = default
) : IAsyncEnumerable<ArchiveProjectedObject>, IAsyncDisposable
{
    private IAsyncDisposable? _lifetime = lifetime;

    public IAsyncEnumerator<ArchiveProjectedObject> GetAsyncEnumerator
    (
        CancellationToken cancellationToken = default
    ) => _objects.GetAsyncEnumerator(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is not null) { await lifetime.DisposeAsync(); }
    }
}

using System.Text.Json;

namespace Yabt.Common.Implementation;

internal sealed class TemporaryIndexLease(SqliteTemporaryIndex index) : TemporaryIndex
{
    private SqliteTemporaryIndex? _index = index;

    public override DiskDictionary<T> CreateDictionary<T>
    (
        JsonSerializerOptions? options = null,
        IEqualityComparer<string>? comparer = null
    ) => Index.CreateDictionary<T>(options, comparer);

    public override DiskEntryCollection<T> CreateCollection<T>(JsonSerializerOptions? options = null) =>
        Index.CreateCollection<T>(options);

    public override void Dispose() => Interlocked.Exchange(ref _index, null)?.ReleaseReference();

    private SqliteTemporaryIndex Index => Volatile.Read(ref _index) ??
        throw new ObjectDisposedException(nameof(TemporaryIndex));
}

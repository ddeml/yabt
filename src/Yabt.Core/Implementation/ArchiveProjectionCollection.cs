using System.Collections;

namespace Yabt.Core.Implementation;

internal sealed class ArchiveProjectionCollection<T>(IEnumerable<T> _items) : IReadOnlyCollection<T>
{
    public int Count => _items.TryGetNonEnumeratedCount(out var count) ? count : _items.Count();

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

using System.Collections;

namespace Yabt.Common.Implementation;

internal sealed class DiskDictionaryValues<T>(DiskDictionary<T> _dictionary) : ICollection<T>
{
    public int Count => _dictionary.Count;
    public bool IsReadOnly => true;
    public bool Contains(T item) => this.Any(value => EqualityComparer<T>.Default.Equals(value, item));
    public IEnumerator<T> GetEnumerator() => _dictionary.Select(pair => pair.Value).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Add(T item) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Remove(T item) => throw new NotSupportedException();
    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
        if (arrayIndex > array.Length || Count > array.Length - arrayIndex) { throw new ArgumentException("Insufficient destination space."); }
        foreach (var item in this) { array[arrayIndex++] = item; }
    }
}

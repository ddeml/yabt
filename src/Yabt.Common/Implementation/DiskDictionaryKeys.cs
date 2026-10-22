using System.Collections;

namespace Yabt.Common.Implementation;

internal sealed class DiskDictionaryKeys<T>(DiskDictionary<T> _dictionary) : ICollection<string>
{
    public int Count => _dictionary.Count;
    public bool IsReadOnly => true;
    public bool Contains(string item) => _dictionary.ContainsKey(item);
    public IEnumerator<string> GetEnumerator() => _dictionary.Select(pair => pair.Key).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Add(string item) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Remove(string item) => throw new NotSupportedException();
    public void CopyTo(string[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
        if (arrayIndex > array.Length || Count > array.Length - arrayIndex) { throw new ArgumentException("Insufficient destination space."); }
        foreach (var item in this) { array[arrayIndex++] = item; }
    }
}

using System.Collections;

namespace Yabt.Common;

/// <summary>A disposable set with provider-owned equality and ordinal enumeration.</summary>
public sealed class DiskSet : IReadOnlySet<string>, ISet<string>, IDisposable
{
    private readonly DiskDictionary<byte> _dictionary;

    internal DiskSet(DiskDictionary<byte> dictionary)
    {
        _dictionary = dictionary;
    }

    public int Count => _dictionary.Count;
    public long LongCount => _dictionary.LongCount;
    public bool IsReadOnly => false;
    public bool Add(string item) => _dictionary.TryAdd(item, 0);
    void ICollection<string>.Add(string item) => Add(item);
    public bool Contains(string item) => _dictionary.ContainsKey(item);
    public bool Remove(string item) => _dictionary.Remove(item);
    public void Clear() => _dictionary.Clear();
    public void CopyTo(string[] array, int arrayIndex) => _dictionary.Keys.CopyTo(array, arrayIndex);
    public IEnumerator<string> GetEnumerator() => _dictionary.Keys.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() => _dictionary.Dispose();

    public bool IsSubsetOf(IEnumerable<string> other)
    {
        using var comparison = CreateComparisonSet(other);
        return LongCount <= comparison.LongCount && this.All(comparison.Contains);
    }

    public bool IsProperSubsetOf(IEnumerable<string> other)
    {
        using var comparison = CreateComparisonSet(other);
        return LongCount < comparison.LongCount && this.All(comparison.Contains);
    }

    public bool IsSupersetOf(IEnumerable<string> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other.All(Contains);
    }

    public bool IsProperSupersetOf(IEnumerable<string> other)
    {
        using var comparison = CreateComparisonSet(other);
        return LongCount > comparison.LongCount && comparison.All(Contains);
    }

    public bool Overlaps(IEnumerable<string> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other.Any(Contains);
    }

    public bool SetEquals(IEnumerable<string> other)
    {
        using var comparison = CreateComparisonSet(other);
        return LongCount == comparison.LongCount && comparison.All(Contains);
    }

    public void UnionWith(IEnumerable<string> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        foreach (var item in other) { Add(item); }
    }

    public void ExceptWith(IEnumerable<string> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other)) { Clear(); return; }
        foreach (var item in other) { Remove(item); }
    }

    public void IntersectWith(IEnumerable<string> other)
    {
        using var comparison = CreateComparisonSet(other);
        using var removals = _dictionary.CreatePeerSet();
        foreach (var item in this)
        {
            if (!comparison.Contains(item)) { removals.Add(item); }
        }

        ExceptWith(removals);
    }

    public void SymmetricExceptWith(IEnumerable<string> other)
    {
        using var comparison = CreateComparisonSet(other);
        foreach (var item in comparison)
        {
            if (!Remove(item)) { Add(item); }
        }
    }

    private DiskSet CreateComparisonSet(IEnumerable<string> other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var comparison = _dictionary.CreatePeerSet();
        try
        {
            comparison.UnionWith(other);
            return comparison;
        }
        catch (Exception)
        {
            comparison.Dispose();
            throw;
        }
    }
}

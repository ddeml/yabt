using System.Collections;
using System.Text.Json;
using Yabt.Common;

namespace Yabt.Metadata.Implementation;

internal sealed class TemporaryManifestEntries<TEntry> : IEnumerable<TEntry>, IDisposable
{
    private readonly TemporaryIndex _index;
    private readonly DiskEntryCollection<TEntry> _entries;
    private readonly bool _ordered;
    private bool _disposed;

    public TemporaryManifestEntries
    (
        TemporaryContentOptions? temporaryOptions,
        JsonSerializerOptions jsonOptions,
        bool ordered
    )
    {
        _index = TemporaryIndex.Create(temporaryOptions);
        _entries = _index.CreateCollection<TEntry>(jsonOptions);
        _ordered = ordered;
    }

    public long LongCount => _entries.LongCount;

    public bool IsCanonical { get; private set; }

    public void Add(TEntry entry, string? key = null)
    {
        if (IsCanonical) { throw new InvalidOperationException("Canonical manifest entries are immutable."); }
        _entries.Add(entry, key);
    }

    public DiskDictionary<byte> CreatePathSet() => _index.CreateDictionary<byte>();

    public void Seal() => IsCanonical = true;

    public IEnumerator<TEntry> GetEnumerator()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return (_ordered ? _entries.EnumerateOrdered() : _entries).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        try { _entries.Dispose(); }
        finally { _index.Dispose(); }
    }
}

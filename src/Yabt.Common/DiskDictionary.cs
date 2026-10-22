using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Yabt.Common.Implementation;

namespace Yabt.Common;

/// <summary>
/// A dictionary whose values and index reside in its owner's temporary database.
/// Keys retain their exact spelling, with optional provider-owned equality.
/// Enumeration uses ordinal order and materializes one value at a time.
/// </summary>
public sealed class DiskDictionary<T> : IDictionary<string, T>, IReadOnlyDictionary<string, T>, IDisposable
{
    private readonly SqliteTemporaryIndex _owner;
    private readonly long _collection;
    private readonly JsonSerializerOptions? _options;
    private readonly IEqualityComparer<string> _comparer;
    private readonly bool _ordinal;
    private long _count;
    private long _version;
    private bool _disposed;

    internal DiskDictionary
    (
        SqliteTemporaryIndex owner,
        long collection,
        JsonSerializerOptions? options,
        IEqualityComparer<string>? comparer
    )
    {
        _owner = owner;
        _collection = collection;
        _options = TemporaryIndexJsonOptions.Create(options);
        _comparer = comparer ?? StringComparer.Ordinal;
        _ordinal = ReferenceEquals(_comparer, StringComparer.Ordinal);
    }

    public int Count => checked((int)LongCount);
    public long LongCount { get { lock (_owner.Gate) { ThrowIfDisposed(); return _count; } } }
    public bool IsReadOnly => false;
    public ICollection<string> Keys => new DiskDictionaryKeys<T>(this);
    public ICollection<T> Values => new DiskDictionaryValues<T>(this);
    IEnumerable<string> IReadOnlyDictionary<string, T>.Keys => Keys;
    IEnumerable<T> IReadOnlyDictionary<string, T>.Values => Values;

    public T this[string key]
    {
        get => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException($"Temporary index key '{key}' was not found.");
        set
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _options);
            lock (_owner.Gate)
            {
                ThrowIfDisposed();
                var storedKey = ResolveStoredKey(key);
                var exists = storedKey is not null && ContainsKey(storedKey);
                using var command = _owner.Command(
                    "INSERT INTO dictionary_items VALUES($collection, $key, $hash, $value) " +
                    "ON CONFLICT(collection, key) DO UPDATE SET value = excluded.value;",
                    _collection,
                    ("$key", SqliteTemporaryIndex.EncodeKey(storedKey ?? key)),
                    ("$hash", _comparer.GetHashCode(key)),
                    ("$value", bytes));
                command.ExecuteNonQuery();
                if (!exists) { _count++; }
                _version++;
            }
        }
    }

    public void Add(string key, T value)
    {
        if (!TryAdd(key, value)) { throw new ArgumentException($"Duplicate temporary index key '{key}'.", nameof(key)); }
    }

    public bool TryAdd(string key, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _options);
        lock (_owner.Gate)
        {
            ThrowIfDisposed();
            if (!_ordinal && ResolveStoredKey(key) is not null) { return false; }
            using var command = _owner.Command(
                "INSERT INTO dictionary_items VALUES($collection, $key, $hash, $value) " +
                "ON CONFLICT(collection, key) DO NOTHING;",
                _collection,
                ("$key", SqliteTemporaryIndex.EncodeKey(key)),
                ("$hash", _comparer.GetHashCode(key)),
                ("$value", bytes));
            if (command.ExecuteNonQuery() == 0) { return false; }
            _count++;
            _version++;
            return true;
        }
    }

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out T value)
    {
        lock (_owner.Gate)
        {
            ThrowIfDisposed();
            var storedKey = ResolveStoredKey(key);
            if (storedKey is null) { value = default; return false; }
            using var command = _owner.Command(
                "SELECT value FROM dictionary_items WHERE collection = $collection AND key = $key;",
                _collection,
                ("$key", SqliteTemporaryIndex.EncodeKey(storedKey)));
            var result = command.ExecuteScalar();
            if (result is byte[] bytes)
            {
                value = JsonSerializer.Deserialize<T>(bytes, _options)!;
                return true;
            }

            value = default;
            return false;
        }
    }

    public bool ContainsKey(string key)
    {
        lock (_owner.Gate)
        {
            ThrowIfDisposed();
            var storedKey = ResolveStoredKey(key);
            if (storedKey is null) { return false; }
            using var command = _owner.Command(
                "SELECT 1 FROM dictionary_items WHERE collection = $collection AND key = $key;",
                _collection,
                ("$key", SqliteTemporaryIndex.EncodeKey(storedKey)));
            return command.ExecuteScalar() is not null;
        }
    }

    public bool Remove(string key)
    {
        lock (_owner.Gate)
        {
            ThrowIfDisposed();
            var storedKey = ResolveStoredKey(key);
            if (storedKey is null) { return false; }
            using var command = _owner.Command(
                "DELETE FROM dictionary_items WHERE collection = $collection AND key = $key;",
                _collection,
                ("$key", SqliteTemporaryIndex.EncodeKey(storedKey)));
            if (command.ExecuteNonQuery() == 0) { return false; }
            _count--;
            _version++;
            return true;
        }
    }

    public bool Remove(string key, [MaybeNullWhen(false)] out T value)
    {
        lock (_owner.Gate)
        {
            if (!TryGetValue(key, out value)) { return false; }
            return Remove(key);
        }
    }

    public void Clear()
    {
        lock (_owner.Gate)
        {
            ThrowIfDisposed();
            using var command = _owner.Command("DELETE FROM dictionary_items WHERE collection = $collection;", _collection);
            command.ExecuteNonQuery();
            _count = 0;
            _version++;
        }
    }

    public IEnumerable<KeyValuePair<string, T>> Enumerate(CancellationToken cancellationToken = default)
    {
        SqliteCommand command;
        SqliteDataReader reader;
        long version;
        lock (_owner.Gate)
        {
            ThrowIfDisposed();
            version = _version;
            command = _owner.Command(
                "SELECT key, value FROM dictionary_items WHERE collection = $collection ORDER BY key;", _collection);
            try { reader = command.ExecuteReader(); }
            catch { command.Dispose(); throw; }
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                KeyValuePair<string, T> item;
                lock (_owner.Gate)
                {
                    ThrowIfDisposed();
                    if (version != _version) { throw new InvalidOperationException("The temporary dictionary changed during enumeration."); }
                    if (!reader.Read()) { yield break; }
                    item = new(
                        SqliteTemporaryIndex.DecodeKey(reader.GetString(0)),
                        JsonSerializer.Deserialize<T>((byte[])reader.GetValue(1), _options)!);
                }

                yield return item;
            }
        }
        finally
        {
            lock (_owner.Gate) { reader.Dispose(); command.Dispose(); }
        }
    }

    public IEnumerator<KeyValuePair<string, T>> GetEnumerator() => Enumerate().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Add(KeyValuePair<string, T> item) => Add(item.Key, item.Value);
    public bool Contains(KeyValuePair<string, T> item) =>
        TryGetValue(item.Key, out var value) && EqualityComparer<T>.Default.Equals(value, item.Value);
    public bool Remove(KeyValuePair<string, T> item)
    {
        lock (_owner.Gate) { return Contains(item) && Remove(item.Key); }
    }

    public void CopyTo(KeyValuePair<string, T>[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
        if (arrayIndex > array.Length || LongCount > array.Length - arrayIndex) { throw new ArgumentException("Insufficient destination space."); }
        foreach (var item in this) { array[arrayIndex++] = item; }
    }

    public void Dispose()
    {
        lock (_owner.Gate)
        {
            if (_disposed) { return; }
            if (!_owner.IsDisposed) { Clear(); }
            _disposed = true;
        }
    }

    internal DiskSet CreatePeerSet() => _owner.CreateSet(_comparer);

    private string? ResolveStoredKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (_ordinal) { return key; }
        using var command = _owner.Command
        (
            "SELECT key FROM dictionary_items WHERE collection = $collection AND comparison_hash = $hash;",
            _collection,
            ("$hash", _comparer.GetHashCode(key))
        );
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var storedKey = SqliteTemporaryIndex.DecodeKey(reader.GetString(0));
            if (_comparer.Equals(storedKey, key)) { return storedKey; }
        }
        return null;
    }

    private void ThrowIfDisposed()
    {
        _owner.ThrowIfDisposed();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

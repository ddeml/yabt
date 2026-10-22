using System.Collections;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Yabt.Common.Implementation;

namespace Yabt.Common;

/// <summary>
/// A replayable sequence whose records and optional ordering keys live in an
/// operation-owned temporary index. Sorting uses the database's disk index.
/// </summary>
public sealed class DiskEntryCollection<T> : IReadOnlyList<T>, IDisposable
{
    private readonly SqliteTemporaryIndex _owner;
    private readonly long _collection;
    private readonly JsonSerializerOptions? _options;
    private long _count;
    private long _version;
    private bool _disposed;

    internal DiskEntryCollection(SqliteTemporaryIndex owner, long collection, JsonSerializerOptions? options)
    {
        _owner = owner;
        _collection = collection;
        _options = TemporaryIndexJsonOptions.Create(options);
    }

    public int Count => checked((int)LongCount);
    public long LongCount { get { lock (_owner.Gate) { ThrowIfDisposed(); return _count; } } }
    public T this[int index] { get => Get(index); set => Set(index, value); }

    public void Add(T value, string? sortKey = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _options);
        lock (_owner.Gate)
        {
            ThrowIfDisposed();
            using var command = _owner.Command(
                "INSERT INTO sequence_items VALUES($collection, $sequence, $key, $value);",
                _collection,
                ("$sequence", _count),
                ("$key", SqliteTemporaryIndex.EncodeKey(sortKey ?? string.Empty)),
                ("$value", bytes));
            command.ExecuteNonQuery();
            _count++;
            _version++;
        }
    }

    public T Get(long index)
    {
        lock (_owner.Gate)
        {
            ValidateIndex(index);
            using var command = _owner.Command(
                "SELECT value FROM sequence_items WHERE collection = $collection AND sequence = $sequence;",
                _collection,
                ("$sequence", index));
            return JsonSerializer.Deserialize<T>((byte[])command.ExecuteScalar()!, _options)!;
        }
    }

    public void Set(long index, T value, string? sortKey = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _options);
        lock (_owner.Gate)
        {
            ValidateIndex(index);
            using var command = sortKey is null
                ? _owner.Command(
                    "UPDATE sequence_items SET value = $value WHERE collection = $collection AND sequence = $sequence;",
                    _collection,
                    ("$sequence", index),
                    ("$value", bytes))
                : _owner.Command(
                    "UPDATE sequence_items SET sort_key = $key, value = $value WHERE collection = $collection AND sequence = $sequence;",
                    _collection,
                    ("$sequence", index),
                    ("$key", SqliteTemporaryIndex.EncodeKey(sortKey)),
                    ("$value", bytes));
            command.ExecuteNonQuery();
            _version++;
        }
    }

    public IEnumerable<T> EnumerateOrdered(CancellationToken cancellationToken = default) =>
        EnumerateCore(ordered: true, cancellationToken);

    public IEnumerable<T> Enumerate(CancellationToken cancellationToken = default) =>
        EnumerateCore(ordered: false, cancellationToken);

    public IEnumerator<T> GetEnumerator() => Enumerate().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Clear()
    {
        lock (_owner.Gate)
        {
            ThrowIfDisposed();
            using var command = _owner.Command("DELETE FROM sequence_items WHERE collection = $collection;", _collection);
            command.ExecuteNonQuery();
            _count = 0;
            _version++;
        }
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

    private IEnumerable<T> EnumerateCore(bool ordered, CancellationToken cancellationToken)
    {
        SqliteCommand command;
        SqliteDataReader reader;
        long version;
        lock (_owner.Gate)
        {
            ThrowIfDisposed();
            version = _version;
            command = _owner.Command(
                "SELECT value FROM sequence_items WHERE collection = $collection ORDER BY " +
                (ordered ? "sort_key, sequence;" : "sequence;"),
                _collection);
            try { reader = command.ExecuteReader(); }
            catch (Exception) { command.Dispose(); throw; }
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                T item;
                lock (_owner.Gate)
                {
                    ThrowIfDisposed();
                    if (version != _version) { throw new InvalidOperationException("The temporary sequence changed during enumeration."); }
                    if (!reader.Read()) { yield break; }
                    item = JsonSerializer.Deserialize<T>((byte[])reader.GetValue(0), _options)!;
                }

                yield return item;
            }
        }
        finally
        {
            lock (_owner.Gate) { reader.Dispose(); command.Dispose(); }
        }
    }

    private void ValidateIndex(long index)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _count);
    }

    private void ThrowIfDisposed()
    {
        _owner.ThrowIfDisposed();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

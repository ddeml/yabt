using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Yabt.Common.Implementation;

internal sealed class SqliteTemporaryIndex : TemporaryIndex
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly string _path;
    private readonly Lock _gate = new();
    private long _nextCollection;
    private long _references = 1;
    private bool _disposed;

    public SqliteTemporaryIndex(TemporaryContentOptions options)
    {
        var directory = options.GetEffectiveTemporaryDirectory();
        TemporaryStorageValidation.ValidateDirectory(directory);
        _path = Path.Combine(directory, $"yabt-index-{Guid.NewGuid():N}.sqlite");
        var fileOptions = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (new FileStream(_path, fileOptions)) { }
        _connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = _path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            Cache = SqliteCacheMode.Private
        }.ToString());
        try
        {
            _connection.Open();
            _connection.CreateCollation("YABT_ORDINAL", StringComparer.Ordinal.Compare);
            using var configure = _connection.CreateCommand();
            configure.CommandText = """
                PRAGMA page_size = 4096;
                PRAGMA cache_size = -2048;
                PRAGMA temp.cache_size = -2048;
                PRAGMA temp_store = FILE;
                PRAGMA mmap_size = 0;
                PRAGMA synchronous = OFF;
                PRAGMA journal_mode = TRUNCATE;
                PRAGMA locking_mode = EXCLUSIVE;
                PRAGMA threads = 0;
                CREATE TABLE dictionary_items (
                    collection INTEGER NOT NULL,
                    key TEXT COLLATE YABT_ORDINAL NOT NULL,
                    comparison_hash INTEGER NOT NULL,
                    value BLOB NOT NULL,
                    PRIMARY KEY (collection, key)
                ) WITHOUT ROWID;
                CREATE INDEX dictionary_hash ON dictionary_items(collection, comparison_hash);
                CREATE TABLE sequence_items (
                    collection INTEGER NOT NULL,
                    sequence INTEGER NOT NULL,
                    sort_key TEXT COLLATE YABT_ORDINAL NOT NULL,
                    value BLOB NOT NULL,
                    PRIMARY KEY (collection, sequence)
                ) WITHOUT ROWID;
                CREATE INDEX sequence_sort ON sequence_items(collection, sort_key, sequence);
                """;
            configure.ExecuteNonQuery();
            _transaction = _connection.BeginTransaction();
        }
        catch (Exception)
        {
            try { _connection.Dispose(); }
            finally { DeleteFiles(); }
            throw;
        }
    }

    public override DiskDictionary<T> CreateDictionary<T>
    (
        JsonSerializerOptions? options = null,
        IEqualityComparer<string>? comparer = null
    )
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return new(this, checked(++_nextCollection), options, comparer);
        }
    }

    public override DiskEntryCollection<T> CreateCollection<T>(JsonSerializerOptions? options = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return new(this, checked(++_nextCollection), options);
        }
    }

    public override void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            try { _transaction.Dispose(); }
            finally
            {
                try { _connection.Dispose(); }
                finally { DeleteFiles(); }
            }
        }
    }

    internal Lock Gate => _gate;

    internal void AddReference()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _references = checked(_references + 1);
        }
    }

    internal void ReleaseReference()
    {
        lock (_gate)
        {
            if (--_references == 0) { Dispose(); }
        }
    }

    internal SqliteCommand Command(string sql, long collection, params (string Name, object Value)[] parameters)
    {
        ThrowIfDisposed();
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$collection", collection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return command;
    }

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal bool IsDisposed => _disposed;

    internal static string EncodeKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        // SQLite binds text as UTF-8. Encoding code units preserves even lone
        // UTF-16 surrogates and makes its text ordering exactly .NET ordinal.
        return string.Create(checked(key.Length * 4), key, static (destination, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                ((ushort)source[index]).TryFormat(destination.Slice(index * 4, 4), out _, "X4", CultureInfo.InvariantCulture);
            }
        });
    }

    internal static string DecodeKey(string key) => string.Create(key.Length / 4, key, static (destination, source) =>
    {
        for (var index = 0; index < destination.Length; index++)
        {
            destination[index] = (char)ushort.Parse(source.AsSpan(index * 4, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
    });

    private void DeleteFiles()
    {
        File.Delete(_path);
        File.Delete(_path + "-journal");
    }
}

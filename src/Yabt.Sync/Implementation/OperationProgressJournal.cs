using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Yabt.Common;
using Yabt.Core.Models;

namespace Yabt.Sync.Implementation;

/// <summary>
/// Persists disposable scheduling hints. A completed row is never evidence that
/// source or destination content still matches, nor permission to skip traversal.
/// The caller supplies an existing, trusted private directory outside operation data.
/// </summary>
internal sealed class OperationProgressJournal : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly string _path;
    private readonly FileStream _lease;
    private readonly SqliteConnection _connection;
    private readonly TimeProvider _timeProvider;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private SqliteTransaction? _transaction;
    private long _lastFlushTimestamp;
    private int _pendingUnits;
    private bool _disposed;

    private OperationProgressJournal
    (
        string path,
        FileStream lease,
        SqliteConnection connection,
        TimeProvider timeProvider,
        int batchSize,
        TimeSpan flushInterval,
        bool isResuming
    )
    {
        _path = path;
        _lease = lease;
        _connection = connection;
        _timeProvider = timeProvider;
        _batchSize = batchSize;
        _flushInterval = flushInterval;
        _lastFlushTimestamp = timeProvider.GetTimestamp();
        IsResuming = isResuming;
    }

    public bool IsResuming { get; }

    internal long QueryCount { get; private set; }

    public long CompletedUnitCount
    {
        get
        {
            using var command = CreateCommand("SELECT completed_units FROM operation WHERE singleton = 1;");
            return (long)command.ExecuteScalar()!;
        }
    }

    public static string GetDatabasePath(string directory, string operationIdentity)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentException.ThrowIfNullOrEmpty(operationIdentity);
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(Encode(operationIdentity))));
        return Path.Combine(Path.GetFullPath(directory), $"{identity}.sqlite");
    }

    public static OperationProgressJournal Open
    (
        string directory,
        string operationIdentity,
        TimeProvider timeProvider,
        int batchSize = 32,
        TimeSpan? flushInterval = null
    )
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var interval = flushInterval ?? TimeSpan.FromSeconds(5);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        var path = GetDatabasePath(directory, operationIdentity);
        if (!Directory.Exists(Path.GetDirectoryName(path)))
        {
            throw new DirectoryNotFoundException("The operation progress directory does not exist.");
        }
        RefuseLinkedFile(path);
        RefuseLinkedFile(path + ".lock");
        RefuseLinkedFile(path + "-journal");
        RefuseLinkedFile(path + "-wal");
        RefuseLinkedFile(path + "-shm");
        NativeFileIdentity.RequireIndependentIfPresent(path);
        NativeFileIdentity.RequireIndependentIfPresent(path + ".lock");
        NativeFileIdentity.RequireIndependentIfPresent(path + "-journal");
        NativeFileIdentity.RequireIndependentIfPresent(path + "-wal");
        NativeFileIdentity.RequireIndependentIfPresent(path + "-shm");

        // Keep the empty lease entry: deleting a lock filename while another
        // process opens it could create two unrelated lock handles.
        var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        SqliteConnection? connection = null;
        try
        {
            NativeFileIdentity.RequireIndependent(lease.SafeFileHandle, path + ".lock");
            var existing = File.Exists(path) && new FileInfo(path).Length != 0;
            connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                Cache = SqliteCacheMode.Private,
                DefaultTimeout = 1,
            }.ToString());
            connection.Open();
            using (var configure = connection.CreateCommand())
            {
                configure.CommandText = """
                    PRAGMA trusted_schema = OFF;
                    PRAGMA cache_size = -2048;
                    PRAGMA temp.cache_size = -2048;
                    PRAGMA temp_store = FILE;
                    PRAGMA mmap_size = 0;
                    PRAGMA synchronous = FULL;
                    PRAGMA journal_mode = DELETE;
                    PRAGMA threads = 0;
                    """;
                configure.ExecuteNonQuery();
            }

            var identityHash = Path.GetFileNameWithoutExtension(path);
            if (!existing)
            {
                using var transaction = connection.BeginTransaction();
                using var initialize = connection.CreateCommand();
                initialize.Transaction = transaction;
                initialize.CommandText = """
                    CREATE TABLE operation (
                        singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                        identity_hash TEXT NOT NULL,
                        completed_units INTEGER NOT NULL CHECK (completed_units >= 0)
                    );
                    CREATE TABLE units (
                        path TEXT COLLATE BINARY PRIMARY KEY NOT NULL,
                        completed INTEGER NOT NULL CHECK (completed IN (0, 1))
                    ) WITHOUT ROWID;
                    CREATE INDEX pending_units ON units(path) WHERE completed = 0;
                    INSERT INTO operation VALUES (1, $identity, 0);
                    INSERT INTO units VALUES ('', 0);
                    PRAGMA user_version = 1;
                    """;
                initialize.Parameters.AddWithValue("$identity", identityHash);
                initialize.ExecuteNonQuery();
                transaction.Commit();
            }
            else
            {
                using var version = connection.CreateCommand();
                version.CommandText = "PRAGMA user_version;";
                if ((long)version.ExecuteScalar()! != SchemaVersion)
                {
                    throw new InvalidDataException("The operation progress schema is unsupported.");
                }
                using var identity = connection.CreateCommand();
                identity.CommandText = "SELECT identity_hash FROM operation WHERE singleton = 1;";
                if (!string.Equals(identity.ExecuteScalar() as string, identityHash, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The operation progress identity does not match.");
                }
            }

            return new(path, lease, connection, timeProvider, batchSize, interval, existing);
        }
        catch (Exception)
        {
            try { connection?.Dispose(); }
            finally { lease.Dispose(); }
            throw;
        }
    }

    public bool WasCompleted(string path)
    {
        QueryCount++;
        using var command = CreateCommand("SELECT completed FROM units WHERE path = $path;");
        command.Parameters.AddWithValue("$path", EncodePath(path));
        return command.ExecuteScalar() is long completed && completed == 1;
    }

    public bool HasUnfinishedSubtree(string path)
    {
        var canonicalPath = ObjectPath.Parse(path).Value;
        QueryCount++;
        using var command = CreateCommand("""
            SELECT NOT EXISTS (SELECT 1 FROM units WHERE path = $path AND completed = 1)
                OR EXISTS (
                    SELECT 1 FROM units
                    WHERE completed = 0 AND path >= $lower AND path < $upper
                );
            """);
        command.Parameters.AddWithValue("$path", EncodePath(canonicalPath));
        command.Parameters.AddWithValue("$lower", canonicalPath.Length == 0 ? "" : Encode(canonicalPath + "/"));
        command.Parameters.AddWithValue("$upper", canonicalPath.Length == 0 ? "G" : Encode(canonicalPath + "0"));
        return (long)command.ExecuteScalar()! != 0;
    }

    public void StartUnit(string path)
    {
        var encoded = EncodePath(path);
        BeginBatch();
        Execute("SAVEPOINT started_unit;");
        try
        {
            using var command = CreateCommand("""
                UPDATE operation SET completed_units = completed_units -
                    COALESCE((SELECT completed FROM units WHERE path = $path), 0);
                INSERT INTO units(path, completed) VALUES ($path, 0)
                    ON CONFLICT(path) DO UPDATE SET completed = 0;
                """);
            command.Parameters.AddWithValue("$path", encoded);
            command.ExecuteNonQuery();
            Execute("RELEASE started_unit;");
        }
        catch (Exception)
        {
            Execute("ROLLBACK TO started_unit; RELEASE started_unit;");
            throw;
        }
        FlushIfDue();
    }

    public void CompleteUnit(string path, IEnumerable<string> children)
    {
        ArgumentNullException.ThrowIfNull(children);
        var parent = ObjectPath.Parse(path);
        BeginBatch();
        Execute("SAVEPOINT completed_unit;");
        try
        {
            using var complete = CreateCommand("""
                UPDATE operation SET completed_units = completed_units + 1 -
                    COALESCE((SELECT completed FROM units WHERE path = $path), 0);
                INSERT INTO units(path, completed) VALUES ($path, 1)
                    ON CONFLICT(path) DO UPDATE SET completed = 1;
                """);
            complete.Parameters.AddWithValue("$path", Encode(parent.Value));
            complete.ExecuteNonQuery();
            using var schedule = CreateCommand("INSERT OR IGNORE INTO units(path, completed) VALUES ($path, 0);");
            var childParameter = schedule.Parameters.Add("$path", SqliteType.Text);
            foreach (var child in children)
            {
                var childPath = ObjectPath.Parse(child);
                if (childPath.IsRoot || childPath.Parent != parent)
                {
                    throw new ArgumentException("Progress children must be exact immediate child paths.", nameof(children));
                }
                childParameter.Value = Encode(childPath.Value);
                schedule.ExecuteNonQuery();
            }
            Execute("RELEASE completed_unit;");
        }
        catch (Exception)
        {
            Execute("ROLLBACK TO completed_unit; RELEASE completed_unit;");
            throw;
        }
        _pendingUnits++;
        FlushIfDue();
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_transaction is null) { return; }
        _transaction.Commit();
        _transaction.Dispose();
        _transaction = null;
        _pendingUnits = 0;
        _lastFlushTimestamp = _timeProvider.GetTimestamp();
    }

    public void CompleteOperation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            try { _transaction?.Dispose(); }
            finally { _connection.Dispose(); }
            NativeFileIdentity.RequireIndependentIfPresent(_path);
            NativeFileIdentity.RequireIndependentIfPresent(_path + "-journal");
            File.Delete(_path);
            File.Delete(_path + "-journal");
        }
        finally
        {
            _disposed = true;
            _lease.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        try { Flush(); }
        finally
        {
            _disposed = true;
            try { _transaction?.Dispose(); }
            finally
            {
                try { _connection.Dispose(); }
                finally { _lease.Dispose(); }
            }
        }
    }

    private void BeginBatch()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _transaction ??= _connection.BeginTransaction();
    }

    private void FlushIfDue()
    {
        if (_pendingUnits >= _batchSize ||
            _timeProvider.GetElapsedTime(_lastFlushTimestamp) >= _flushInterval)
        {
            Flush();
        }
    }

    private SqliteCommand CreateCommand(string sql)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var command = _connection.CreateCommand();
        command.Transaction = _transaction;
        command.CommandText = sql;
        return command;
    }

    private void Execute(string sql)
    {
        using var command = CreateCommand(sql);
        command.ExecuteNonQuery();
    }

    private static string EncodePath(string path) => Encode(ObjectPath.Parse(path).Value);

    private static string Encode(string value) => string.Create
    (
        checked(value.Length * 4), value, static (destination, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                ((ushort)source[index]).TryFormat(destination.Slice(index * 4, 4), out _, "X4", CultureInfo.InvariantCulture);
            }
        }
    );

    private static void RefuseLinkedFile(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null || info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Operation progress files cannot be symbolic links or reparse points.");
        }
    }
}

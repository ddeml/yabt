using Microsoft.Data.Sqlite;

namespace Yabt.Sync;

public sealed class SqliteOperationalCache(string _databasePath)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS CacheEntries (
                CacheKey TEXT NOT NULL PRIMARY KEY,
                CacheValueJson TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

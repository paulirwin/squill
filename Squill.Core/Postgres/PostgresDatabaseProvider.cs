using Npgsql;

namespace Squill.Core.Postgres;

public class PostgresDatabaseProvider : IDatabaseProvider
{
    private readonly string _connectionString;

    public PostgresDatabaseProvider(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<IDatabase> CreateTemporaryModelDatabaseAsync(CancellationToken cancellationToken)
    {
        var dbName = $"squill_model_{Guid.NewGuid():n}";

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {dbName} WITH OWNER = postgres", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return new PostgresDatabase(_connectionString, dbName);
    }
}
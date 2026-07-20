using Npgsql;
using Squill.Core;

namespace Squill.Provider.Postgres;

public class PostgresDatabaseProvider : IDatabaseProvider
{
    private readonly string _connectionString;

    public PostgresDatabaseProvider(string connectionString)
    {
        _connectionString = connectionString;
    }

    public Task<IDatabase> CreateTemporaryModelDatabaseAsync(CancellationToken cancellationToken)
    {
        var dbName = $"squill_model_{Guid.NewGuid():n}";

        return CreateDatabaseAsync(dbName, cancellationToken);
    }

    public async Task<IDatabase> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name} WITH OWNER = postgres", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return new PostgresDatabase(_connectionString, name);
    }

    public IDatabaseModelBuilder CreateDatabaseModelBuilder(IDatabase database) 
        => new PostgresDatabaseModelBuilder(database);

    public IDatabaseDependencyAnalyzer DependencyAnalyzer { get; } = new PostgresDatabaseDependencyAnalyzer();

    public ITableDiffAnalyzer TableDiffAnalyzer { get; } = new PostgresTableDiffAnalyzer();
}
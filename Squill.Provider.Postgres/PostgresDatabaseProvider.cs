using Npgsql;
using Squill.Core;

namespace Squill.Provider.Postgres;

public class PostgresDatabaseProvider : DatabaseProviderBase
{
    public PostgresDatabaseProvider(string connectionString) : base(connectionString)
    {
    }

    public override async Task<IDatabase> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name} WITH OWNER = postgres", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return new PostgresDatabase(ConnectionString, name);
    }

    public override IDatabaseModelBuilder CreateDatabaseModelBuilder(IDatabase database)
        => new PostgresDatabaseModelBuilder(database);

    public override IDatabaseDependencyAnalyzer DependencyAnalyzer { get; } = new PostgresDatabaseDependencyAnalyzer();

    public override ITableDiffAnalyzer TableDiffAnalyzer { get; } = new PostgresTableDiffAnalyzer();
}

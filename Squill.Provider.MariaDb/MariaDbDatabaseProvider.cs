using MySqlConnector;
using Squill.Core;

namespace Squill.Provider.MariaDb;

/// <summary>
/// The MariaDB (and MySQL) implementation of <see cref="IDatabaseProvider"/>. Creates
/// temporary/named databases and exposes the model builder, dependency analyzer, and
/// table-diff analyzer for the provider. The connection-string handling and temporary-database
/// creation live in <see cref="DatabaseProviderBase"/>.
/// </summary>
public class MariaDbDatabaseProvider : DatabaseProviderBase
{
    public MariaDbDatabaseProvider(string connectionString) : base(connectionString)
    {
    }

    public override async Task<IDatabase> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var conn = new MySqlConnection(ConnectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new MySqlCommand($"CREATE DATABASE `{name}`;", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return new MariaDbDatabase(ConnectionString, name);
    }

    public override IDatabaseModelBuilder CreateDatabaseModelBuilder(IDatabase database)
        => new MariaDbDatabaseModelBuilder(database);

    public override IDatabaseDependencyAnalyzer DependencyAnalyzer { get; } = new MariaDbDatabaseDependencyAnalyzer();

    public override ITableDiffAnalyzer TableDiffAnalyzer { get; } = new MariaDbTableDiffAnalyzer();
}

using MySqlConnector;
using Squill.Core;

namespace Squill.Provider.MariaDb;

/// <summary>
/// The MariaDB (and MySQL) implementation of <see cref="IDatabaseProvider"/>. Holds a
/// connection string, creates temporary/named databases, and exposes the model builder,
/// dependency analyzer, and table-diff analyzer for the provider.
/// </summary>
public class MariaDbDatabaseProvider : IDatabaseProvider
{
    private readonly string _connectionString;

    public MariaDbDatabaseProvider(string connectionString)
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
        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);
        await using var cmd = new MySqlCommand($"CREATE DATABASE `{name}`;", conn);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return new MariaDbDatabase(_connectionString, name);
    }

    public IDatabaseModelBuilder CreateDatabaseModelBuilder(IDatabase database)
        => new MariaDbDatabaseModelBuilder(database);

    public IDatabaseDependencyAnalyzer DependencyAnalyzer { get; } = new MariaDbDatabaseDependencyAnalyzer();

    public ITableDiffAnalyzer TableDiffAnalyzer { get; } = new MariaDbTableDiffAnalyzer();
}

namespace Squill.Core;

/// <summary>
/// Shared plumbing for <see cref="IDatabaseProvider"/>: holds the connection string and
/// implements <see cref="CreateTemporaryModelDatabaseAsync"/> (a uniquely-named database via
/// <see cref="CreateDatabaseAsync"/>) once for every provider. The engine-specific parts —
/// how a database is created (ADO driver and <c>CREATE DATABASE</c> dialect), the model
/// builder, and the dependency/table-diff analyzers — stay abstract.
/// </summary>
public abstract class DatabaseProviderBase : IDatabaseProvider
{
    protected DatabaseProviderBase(string connectionString)
    {
        ConnectionString = connectionString;
    }

    protected string ConnectionString { get; }

    public Task<IDatabase> CreateTemporaryModelDatabaseAsync(CancellationToken cancellationToken)
        => CreateDatabaseAsync($"squill_model_{Guid.NewGuid():n}", cancellationToken);

    public abstract Task<IDatabase> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default);

    public abstract IDatabaseModelBuilder CreateDatabaseModelBuilder(IDatabase database);

    public abstract IDatabaseDependencyAnalyzer DependencyAnalyzer { get; }

    public abstract ITableDiffAnalyzer TableDiffAnalyzer { get; }
}

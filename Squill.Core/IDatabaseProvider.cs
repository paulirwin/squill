namespace Squill.Core;

public interface IDatabaseProvider
{
    Task<IDatabase> CreateTemporaryModelDatabaseAsync(CancellationToken cancellationToken);
    
    Task<IDatabase> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default);

    IDatabaseModelBuilder CreateDatabaseModelBuilder(IDatabase database);
    
    IDatabaseDependencyAnalyzer DependencyAnalyzer { get; }
}
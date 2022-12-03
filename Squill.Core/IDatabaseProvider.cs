namespace Squill.Core;

public interface IDatabaseProvider
{
    Task<IDatabase> CreateTemporaryModelDatabaseAsync(CancellationToken cancellationToken);
}
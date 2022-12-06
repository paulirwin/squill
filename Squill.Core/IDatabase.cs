namespace Squill.Core;

public interface IDatabase : IDisposable, IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    
    Task RunScriptAsync(string sql, CancellationToken cancellationToken = default);
    
    Task<Model> ExtractModelAsync(CancellationToken cancellationToken = default);

    Task DropAsync(CancellationToken cancellationToken = default);
    
    Task PublishAsync(SchemaComparison comparison, CancellationToken cancellationToken = default);
}
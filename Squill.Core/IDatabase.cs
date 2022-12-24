using System.Data.Common;

namespace Squill.Core;

public interface IDatabase : IDisposable, IAsyncDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    
    Task RunScriptAsync(string sql,
        IReadOnlyList<IDatabaseParameter>? parameters = null,
        CancellationToken cancellationToken = default);

    Task<DbDataReader> RunScriptReaderAsync(string sql,
        IReadOnlyList<IDatabaseParameter>? parameters = null,
        CancellationToken cancellationToken = default);
    
    Task DropAsync(CancellationToken cancellationToken = default);
    
    Task PublishAsync(SchemaComparison comparison, CancellationToken cancellationToken = default);
    
    string Name { get; }
}
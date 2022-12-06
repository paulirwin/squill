namespace Squill.Core;

public interface IDatabaseProvider
{
    Task<IDatabase> CreateTemporaryModelDatabaseAsync(CancellationToken cancellationToken);
    
    Task<IDatabase> CreateDatabaseAsync(string name, CancellationToken cancellationToken = default);
    
    bool IsDependentElementType(string type);
    
    IList<Element>? GetDependentElements(Element sourceElement, Model model);
}
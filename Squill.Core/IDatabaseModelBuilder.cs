namespace Squill.Core;

public interface IDatabaseModelBuilder
{
    Task<Model> ExtractModelAsync(CancellationToken cancellationToken = default);
}
namespace Squill.Core;

/// <summary>
/// Builds a <see cref="Model"/> from declarative SQL source in a <see cref="Workspace"/>.
/// Distinct from <see cref="IDatabaseModelBuilder"/>, which extracts a model from a live
/// database: compiling source can produce warnings about constructs that were declared but
/// not modeled, so it returns a <see cref="BuildResult"/> rather than a bare model. A live
/// database has no such notion — whatever is in the catalog is the model.
/// </summary>
public interface IWorkspaceModelBuilder
{
    /// <summary>
    /// Parses and validates the workspace's source files into a model, along with any
    /// non-fatal diagnostics. Errors are thrown (as <see cref="SqlSourceException"/>, or an
    /// <see cref="AggregateException"/> of them when a build has several).
    /// </summary>
    Task<BuildResult> ExtractModelAsync(CancellationToken cancellationToken = default);
}

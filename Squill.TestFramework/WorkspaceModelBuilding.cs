using Squill.Core;

namespace Squill.TestFramework;

/// <summary>
/// Builds a <see cref="Model"/> from a single inline SQL string, the shape re-declared as a
/// private <c>BuildModelAsync</c> across the provider test projects. The provider's model
/// builder (each provider has its own ANTLR parser) is supplied as a factory, so this stays
/// provider-agnostic.
/// </summary>
public static class WorkspaceModelBuilding
{
    /// <summary>
    /// Wraps <paramref name="sql"/> in a one-file workspace and extracts a model from it using
    /// the builder from <paramref name="modelBuilderFactory"/>.
    /// </summary>
    public static async Task<Model> BuildModelAsync(
        string sql,
        Func<Workspace, IWorkspaceModelBuilder> modelBuilderFactory,
        CancellationToken cancellationToken = default)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return (await modelBuilderFactory(workspace).ExtractModelAsync(cancellationToken)).Model;
    }
}

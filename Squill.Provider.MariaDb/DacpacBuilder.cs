using Squill.Core;
using Squill.Dacpac;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Builds a DACPAC from a workspace of declarative MariaDB SQL source files, using the
/// ANTLR-based parser (no live database required). Mirrors the Postgres provider's build path
/// so both hosts (the MSBuild task and the console <c>build</c> verb) produce the same DACPAC
/// from the same inputs. The build itself is provider-agnostic (see
/// <see cref="WorkspaceDacpacBuilder"/>); this type only supplies the MariaDB model builder.
/// </summary>
public static class DacpacBuilder
{
    private static Func<Workspace, IWorkspaceModelBuilder> CreateModelBuilder(MariaDbEngine engine) =>
        workspace => new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser(), engine);

    /// <summary>
    /// Which engine a DACPAC's recorded provider name selects. The two are one provider but not
    /// one dialect: a handful of constructs canonicalize differently on each (issue #147), so
    /// the build has to resolve this rather than assume.
    /// </summary>
    internal static MariaDbEngine EngineOf(string? providerName) =>
        string.Equals(providerName, "MySql", StringComparison.OrdinalIgnoreCase)
            ? MariaDbEngine.MySql
            : MariaDbEngine.MariaDb;

    /// <inheritdoc cref="WorkspaceDacpacBuilder.BuildAsync"/>
    public static Task BuildAsync(
        Workspace workspace,
        ModelMetadata metadata,
        Stream stream,
        CancellationToken cancellationToken = default) =>
        WorkspaceDacpacBuilder.BuildAsync(workspace, metadata, stream,
            CreateModelBuilder(EngineOf(metadata.ProviderName)), cancellationToken);

    /// <inheritdoc cref="WorkspaceDacpacBuilder.BuildToFileAsync"/>
    public static Task BuildToFileAsync(
        Workspace workspace,
        ModelMetadata metadata,
        string outputPath,
        CancellationToken cancellationToken = default) =>
        WorkspaceDacpacBuilder.BuildToFileAsync(workspace, metadata, outputPath,
            CreateModelBuilder(EngineOf(metadata.ProviderName)), cancellationToken);

    /// <inheritdoc cref="WorkspaceDacpacBuilder.BuildModelAsync"/>
    /// <param name="engine">
    /// The target engine. Required for the same reason it is on
    /// <see cref="ParserWorkspaceModelBuilder"/>: assuming the wrong one silently produces a
    /// model that re-diffs against its own database forever.
    /// </param>
    public static Task<BuildResult> BuildModelAsync(
        Workspace workspace,
        MariaDbEngine engine,
        CancellationToken cancellationToken = default) =>
        WorkspaceDacpacBuilder.BuildModelAsync(workspace, CreateModelBuilder(engine), cancellationToken);

    /// <inheritdoc cref="WorkspaceDacpacBuilder.CreateWorkspace"/>
    public static Workspace CreateWorkspace(IEnumerable<string> sourceFilePaths) =>
        WorkspaceDacpacBuilder.CreateWorkspace(sourceFilePaths);
}

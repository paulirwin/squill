using Squill.Core;
using Squill.Dacpac;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres;

/// <summary>
/// Builds a DACPAC from a workspace of declarative SQL source files, using the ANTLR-based
/// PostgreSQL parser (no live database required). This is the shared build path used by both
/// the MSBuild task (<c>Squill.Build</c>) and the console <c>build</c> verb, so both produce
/// byte-identical DACPACs from the same inputs. The build itself is provider-agnostic (see
/// <see cref="WorkspaceDacpacBuilder"/>); this type only supplies the PostgreSQL model builder.
/// </summary>
public static class DacpacBuilder
{
    private static IWorkspaceModelBuilder CreateModelBuilder(Workspace workspace) =>
        new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser());

    /// <inheritdoc cref="WorkspaceDacpacBuilder.BuildAsync"/>
    public static Task BuildAsync(
        Workspace workspace,
        ModelMetadata metadata,
        Stream stream,
        CancellationToken cancellationToken = default) =>
        WorkspaceDacpacBuilder.BuildAsync(workspace, metadata, stream, CreateModelBuilder, cancellationToken);

    /// <inheritdoc cref="WorkspaceDacpacBuilder.BuildToFileAsync"/>
    public static Task BuildToFileAsync(
        Workspace workspace,
        ModelMetadata metadata,
        string outputPath,
        CancellationToken cancellationToken = default) =>
        WorkspaceDacpacBuilder.BuildToFileAsync(workspace, metadata, outputPath, CreateModelBuilder, cancellationToken);

    /// <inheritdoc cref="WorkspaceDacpacBuilder.BuildModelAsync"/>
    public static Task<BuildResult> BuildModelAsync(
        Workspace workspace,
        CancellationToken cancellationToken = default) =>
        WorkspaceDacpacBuilder.BuildModelAsync(workspace, CreateModelBuilder, cancellationToken);

    /// <inheritdoc cref="WorkspaceDacpacBuilder.CreateWorkspace"/>
    public static Workspace CreateWorkspace(IEnumerable<string> sourceFilePaths) =>
        WorkspaceDacpacBuilder.CreateWorkspace(sourceFilePaths);
}

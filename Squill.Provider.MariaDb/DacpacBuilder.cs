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
    private static IWorkspaceModelBuilder CreateModelBuilder(Workspace workspace) =>
        new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser());

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

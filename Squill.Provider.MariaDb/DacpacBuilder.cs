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
    private static Func<Workspace, IWorkspaceModelBuilder> CreateModelBuilder(
        MariaDbFamilyDatabaseSchemaProvider schemaProvider) =>
        workspace => new ParserWorkspaceModelBuilder(
            workspace, new AntlrMariaDbParser(), schemaProvider);

    /// <summary>
    /// The schema provider a DACPAC's recorded provider name and target version select. The two
    /// engines are one provider but not one dialect: a handful of constructs canonicalize
    /// differently on each (issue #147), and the schema provider is what declares which.
    /// An unspecified target version resolves to the engine's latest supported major.
    /// </summary>
    public static MariaDbFamilyDatabaseSchemaProvider SchemaProviderFor(
        string providerName, int? targetMajorVersion)
    {
        var schemaProvider =
            DatabaseSchemaProviderRegistry.Resolve(providerName, targetMajorVersion);

        // A name this builder does not serve resolves to some other engine's provider. Say so,
        // rather than letting it surface as a bare InvalidCastException.
        return schemaProvider as MariaDbFamilyDatabaseSchemaProvider
            ?? throw new ArgumentException(
                $"'{providerName}' is not a MariaDB or MySQL provider name; it resolves to "
                + $"{schemaProvider.GetType().Name}. Use the provider that serves that engine.",
                nameof(providerName));
    }

    /// <inheritdoc cref="WorkspaceDacpacBuilder.BuildAsync"/>
    public static Task BuildAsync(
        Workspace workspace,
        ModelMetadata metadata,
        Stream stream,
        CancellationToken cancellationToken = default) =>
        WorkspaceDacpacBuilder.BuildAsync(workspace, metadata, stream,
            CreateModelBuilder(SchemaProviderFor(metadata.ProviderName, metadata.TargetMajorVersion)),
            cancellationToken);

    /// <inheritdoc cref="WorkspaceDacpacBuilder.BuildToFileAsync"/>
    public static Task BuildToFileAsync(
        Workspace workspace,
        ModelMetadata metadata,
        string outputPath,
        CancellationToken cancellationToken = default) =>
        WorkspaceDacpacBuilder.BuildToFileAsync(workspace, metadata, outputPath,
            CreateModelBuilder(SchemaProviderFor(metadata.ProviderName, metadata.TargetMajorVersion)),
            cancellationToken);

    /// <inheritdoc cref="WorkspaceDacpacBuilder.BuildModelAsync"/>
    /// <param name="schemaProvider">
    /// The target engine's schema provider. Required for the same reason it is on
    /// <see cref="ParserWorkspaceModelBuilder"/>: assuming the wrong engine silently produces a
    /// model that re-diffs against its own database forever.
    /// </param>
    public static Task<BuildResult> BuildModelAsync(
        Workspace workspace,
        MariaDbFamilyDatabaseSchemaProvider schemaProvider,
        CancellationToken cancellationToken = default) =>
        WorkspaceDacpacBuilder.BuildModelAsync(
            workspace, CreateModelBuilder(schemaProvider), cancellationToken);

    /// <inheritdoc cref="WorkspaceDacpacBuilder.CreateWorkspace"/>
    public static Workspace CreateWorkspace(IEnumerable<string> sourceFilePaths) =>
        WorkspaceDacpacBuilder.CreateWorkspace(sourceFilePaths);
}

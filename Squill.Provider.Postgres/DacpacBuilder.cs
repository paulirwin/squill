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

    private static Func<Workspace, IWorkspaceModelBuilder> CreateModelBuilder(
        PostgresqlDatabaseSchemaProvider schemaProvider) =>
        workspace => new ParserWorkspaceModelBuilder(
            workspace, new AntlrPostgresParser(), schemaProvider);

    /// <summary>
    /// The schema provider a DACPAC's recorded provider name and target version select, so the
    /// build can report constructs newer than the targeted major (issue #142). An unspecified
    /// target version resolves to the latest supported major, which is what declaring no
    /// <c>SquillTargetVersion</c> means — an unconstrained project behaves as if it targets a
    /// current server. Mirrors the MariaDB provider's builder of the same name.
    ///
    /// <para>
    /// Takes the whole target version, not just its major. PostgreSQL's own feature boundaries
    /// fall on majors, so nothing here gates below one today; carrying the full version keeps the
    /// build path uniform across providers and gives a future point-release gate somewhere to
    /// read from (issue #189).
    /// </para>
    /// </summary>
    public static PostgresqlDatabaseSchemaProvider SchemaProviderFor(
        string providerName, TargetVersion? targetVersion)
        => AsPostgresProvider(
            providerName, DatabaseSchemaProviderRegistry.Resolve(providerName, targetVersion));

    private static PostgresqlDatabaseSchemaProvider AsPostgresProvider(
        string providerName, DatabaseSchemaProvider schemaProvider)
        // A name this builder does not serve resolves to some other engine's provider. Say so,
        // rather than letting it surface as a bare InvalidCastException.
        => schemaProvider as PostgresqlDatabaseSchemaProvider
            ?? throw new ArgumentException(
                $"'{providerName}' is not a PostgreSQL provider name; it resolves to "
                + $"{schemaProvider.GetType().Name}. Use the provider that serves that engine.",
                nameof(providerName));

    /// <inheritdoc cref="WorkspaceDacpacBuilder.BuildAsync"/>
    public static Task BuildAsync(
        Workspace workspace,
        ModelMetadata metadata,
        Stream stream,
        CancellationToken cancellationToken = default) =>
        WorkspaceDacpacBuilder.BuildAsync(workspace, metadata, stream,
            CreateModelBuilder(SchemaProviderFor(metadata.ProviderName, metadata.TargetVersion)),
            cancellationToken);

    /// <inheritdoc cref="WorkspaceDacpacBuilder.BuildToFileAsync"/>
    public static Task BuildToFileAsync(
        Workspace workspace,
        ModelMetadata metadata,
        string outputPath,
        CancellationToken cancellationToken = default) =>
        WorkspaceDacpacBuilder.BuildToFileAsync(workspace, metadata, outputPath,
            CreateModelBuilder(SchemaProviderFor(metadata.ProviderName, metadata.TargetVersion)),
            cancellationToken);

    /// <inheritdoc cref="WorkspaceDacpacBuilder.BuildModelAsync"/>
    public static Task<BuildResult> BuildModelAsync(
        Workspace workspace,
        CancellationToken cancellationToken = default) =>
        WorkspaceDacpacBuilder.BuildModelAsync(workspace, CreateModelBuilder, cancellationToken);

    /// <summary>
    /// Builds the model for a given target, so constructs newer than that major are reported
    /// (issue #142). The overload without a schema provider targets the latest supported major.
    /// </summary>
    public static Task<BuildResult> BuildModelAsync(
        Workspace workspace,
        PostgresqlDatabaseSchemaProvider schemaProvider,
        CancellationToken cancellationToken = default) =>
        WorkspaceDacpacBuilder.BuildModelAsync(
            workspace, CreateModelBuilder(schemaProvider), cancellationToken);

    /// <inheritdoc cref="WorkspaceDacpacBuilder.CreateWorkspace"/>
    public static Workspace CreateWorkspace(IEnumerable<string> sourceFilePaths) =>
        WorkspaceDacpacBuilder.CreateWorkspace(sourceFilePaths);
}

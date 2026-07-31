using Squill.Core;
using Squill.Dacpac;

namespace Squill.Provider.Postgres;

/// <summary>
/// Adapts the PostgreSQL provider to the host-facing <see cref="ISquillProvider"/> so the CLI
/// and MSBuild task can dispatch to it by provider name. Answers to <c>Postgresql</c>. The
/// build/deploy/script plumbing lives in <see cref="SquillProviderBase"/>; this type binds the
/// two hooks to the PostgreSQL builder and deployer.
/// </summary>
public sealed class PostgresSquillProvider : SquillProviderBase
{
    public override string Name => "Postgresql";

    public override bool Matches(string providerName)
        => string.Equals(providerName, "Postgresql", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerName, "Postgres", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerName, "PostgreSQL", StringComparison.OrdinalIgnoreCase);

    protected override Task<BuildResult> BuildModelCoreAsync(
        Workspace workspace, ModelMetadata metadata, CancellationToken cancellationToken)
        => DacpacBuilder.BuildModelAsync(
            workspace,
            DacpacBuilder.SchemaProviderFor(metadata.ProviderName, metadata.TargetVersion),
            cancellationToken);

    protected override async Task<Squill.Dacpac.DeployResult> DeployCoreAsync(
        Stream dacpacStream,
        string connectionString,
        string? targetDatabaseName,
        bool dryRun,
        IProgress<string>? progress,
        DeployOptions? options,
        CancellationToken cancellationToken)
    {
        var result = await DacpacDeployer.DeployAsync(
            dacpacStream, connectionString, targetDatabaseName, dryRun, progress, options,
            cancellationToken);

        return new Squill.Dacpac.DeployResult(result.Script, result.WasExecuted);
    }
}

using Squill.Core;
using Squill.Dacpac;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Adapts the MariaDB/MySQL provider to the host-facing <see cref="ISquillProvider"/> so the
/// CLI and MSBuild task can dispatch to it by provider name. One provider serves both engines,
/// so it answers to both <c>MariaDb</c> and <c>MySql</c> (case-insensitive). The
/// build/deploy/script plumbing lives in <see cref="SquillProviderBase"/>; this type binds the
/// two hooks to the MariaDB builder and deployer.
/// </summary>
public sealed class MariaDbSquillProvider : SquillProviderBase
{
    public override string Name => "MariaDb";

    public override bool Matches(string providerName)
        => string.Equals(providerName, "MariaDb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerName, "MySql", StringComparison.OrdinalIgnoreCase);

    // One provider, two engines: which name the project selected decides which dialect to
    // build for, since a few constructs canonicalize differently on each (issue #147).
    protected override Task<BuildResult> BuildModelCoreAsync(
        Workspace workspace, string providerName, CancellationToken cancellationToken)
        => DacpacBuilder.BuildModelAsync(
            workspace, DacpacBuilder.EngineOf(providerName), cancellationToken);

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

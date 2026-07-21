using Squill.Core;
using Squill.Dacpac;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Adapts the MariaDB/MySQL provider to the host-facing <see cref="ISquillProvider"/> so the
/// CLI and MSBuild task can dispatch to it by provider name. One provider serves both
/// engines, so it answers to both <c>MariaDb</c> and <c>MySql</c> (case-insensitive).
/// </summary>
public sealed class MariaDbSquillProvider : ISquillProvider
{
    public string Name => "MariaDb";

    public bool Matches(string providerName)
        => string.Equals(providerName, "MariaDb", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerName, "MySql", StringComparison.OrdinalIgnoreCase);

    public Task<Model> BuildModelAsync(Workspace workspace, CancellationToken cancellationToken = default)
        => DacpacBuilder.BuildModelAsync(workspace, cancellationToken);

    public async Task<Squill.Dacpac.DeployResult> DeployAsync(
        Stream dacpacStream,
        string connectionString,
        string? targetDatabaseName,
        bool dryRun,
        IProgress<string>? progress,
        DeployOptions? options,
        CancellationToken cancellationToken = default)
    {
        var result = await DacpacDeployer.DeployAsync(
            dacpacStream, connectionString, targetDatabaseName, dryRun, progress, options,
            cancellationToken);

        return new Squill.Dacpac.DeployResult(result.Script, result.WasExecuted);
    }

    public async Task<Squill.Dacpac.DeployResult> ScriptAsync(
        Stream dacpacStream,
        string connectionString,
        string? targetDatabaseName,
        IProgress<string>? progress,
        DeployOptions? options,
        CancellationToken cancellationToken = default)
    {
        var result = await DacpacDeployer.DeployAsync(
            dacpacStream, connectionString, targetDatabaseName, dryRun: true, progress, options,
            cancellationToken);

        return new Squill.Dacpac.DeployResult(result.Script, result.WasExecuted);
    }
}

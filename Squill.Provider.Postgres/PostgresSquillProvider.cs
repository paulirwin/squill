using Squill.Core;
using Squill.Dacpac;

namespace Squill.Provider.Postgres;

/// <summary>
/// Adapts the PostgreSQL provider to the host-facing <see cref="ISquillProvider"/> so the
/// CLI and MSBuild task can dispatch to it by provider name. Answers to <c>Postgresql</c>.
/// </summary>
public sealed class PostgresSquillProvider : ISquillProvider
{
    public string Name => "Postgresql";

    public bool Matches(string providerName)
        => string.Equals(providerName, "Postgresql", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerName, "Postgres", StringComparison.OrdinalIgnoreCase)
            || string.Equals(providerName, "PostgreSQL", StringComparison.OrdinalIgnoreCase);

    public Task<BuildResult> BuildModelAsync(Workspace workspace, CancellationToken cancellationToken = default)
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
        // Scripting is a dry-run deploy: diff and script, but never execute.
        var result = await DacpacDeployer.DeployAsync(
            dacpacStream, connectionString, targetDatabaseName, dryRun: true, progress, options,
            cancellationToken);

        return new Squill.Dacpac.DeployResult(result.Script, result.WasExecuted);
    }
}

using Squill.Core;

namespace Squill.Dacpac;

/// <summary>
/// The shared host-facing provider adapter. <see cref="ScriptAsync"/> is a dry-run deploy for
/// every provider, and <see cref="BuildModelAsync"/> / <see cref="DeployAsync"/> are pure
/// pass-throughs to the provider's own build and deploy entry points. Concrete providers
/// supply only the identity (<see cref="Name"/> / <see cref="Matches"/>) and those two hooks
/// (<see cref="BuildModelCoreAsync"/> / <see cref="DeployCoreAsync"/>), which bind to the
/// provider's parser, model builder, and deployer.
/// </summary>
public abstract class SquillProviderBase : ISquillProvider
{
    public abstract string Name { get; }

    public abstract bool Matches(string providerName);

    /// <summary>Builds a model from the workspace using this provider's parser/model builder.</summary>
    protected abstract Task<BuildResult> BuildModelCoreAsync(
        Workspace workspace, CancellationToken cancellationToken);

    /// <summary>Deploys (or, when <paramref name="dryRun"/>, scripts) the DACPAC via this provider's deployer.</summary>
    protected abstract Task<DeployResult> DeployCoreAsync(
        Stream dacpacStream,
        string connectionString,
        string? targetDatabaseName,
        bool dryRun,
        IProgress<string>? progress,
        DeployOptions? options,
        CancellationToken cancellationToken);

    public Task<BuildResult> BuildModelAsync(Workspace workspace, CancellationToken cancellationToken = default)
        => BuildModelCoreAsync(workspace, cancellationToken);

    public Task<DeployResult> DeployAsync(
        Stream dacpacStream,
        string connectionString,
        string? targetDatabaseName,
        bool dryRun,
        IProgress<string>? progress,
        DeployOptions? options,
        CancellationToken cancellationToken = default)
        => DeployCoreAsync(
            dacpacStream, connectionString, targetDatabaseName, dryRun, progress, options,
            cancellationToken);

    public Task<DeployResult> ScriptAsync(
        Stream dacpacStream,
        string connectionString,
        string? targetDatabaseName,
        IProgress<string>? progress,
        DeployOptions? options,
        CancellationToken cancellationToken = default)
        // Scripting is a dry-run deploy: diff and script, but never execute.
        => DeployCoreAsync(
            dacpacStream, connectionString, targetDatabaseName, dryRun: true, progress, options,
            cancellationToken);
}

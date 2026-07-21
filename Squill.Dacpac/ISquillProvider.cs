using Squill.Core;

namespace Squill.Dacpac;

/// <summary>
/// The result of a deploy: the generated SQL script and whether it was executed. On a dry
/// run the script is generated and returned but not run. Shared across providers so the
/// CLI and hosts don't depend on any one provider's result type.
/// </summary>
public readonly record struct DeployResult(string Script, bool WasExecuted);

/// <summary>
/// A database provider as seen by the build/deploy hosts (the CLI and the MSBuild task):
/// it knows which provider name(s) it answers to, how to build a model from declarative SQL
/// source, and how to deploy or script a DACPAC against a target database. This abstraction
/// lets a single host dispatch to the right provider based on the DACPAC's recorded provider
/// name, rather than hardcoding one provider.
/// </summary>
public interface ISquillProvider
{
    /// <summary>The canonical provider name (e.g. <c>Postgresql</c>, <c>MariaDb</c>).</summary>
    string Name { get; }

    /// <summary>
    /// Whether this provider answers to <paramref name="providerName"/> (case-insensitive).
    /// A provider may match more than one name — e.g. the MariaDB provider matches both
    /// <c>MariaDb</c> and <c>MySql</c>, since one provider serves both engines.
    /// </summary>
    bool Matches(string providerName);

    /// <summary>
    /// Builds a <see cref="Model"/> from the declarative SQL files in
    /// <paramref name="workspace"/>, using this provider's parser (no live database needed).
    /// </summary>
    Task<Model> BuildModelAsync(Workspace workspace, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deploys the DACPAC read from <paramref name="dacpacStream"/> to the target database:
    /// extract the target's current model, diff it against the DACPAC's model, and run the
    /// changes (or, on a dry run, just script them).
    /// </summary>
    Task<DeployResult> DeployAsync(
        Stream dacpacStream,
        string connectionString,
        string? targetDatabaseName,
        bool dryRun,
        IProgress<string>? progress,
        DeployOptions? options,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates the deployment script for the DACPAC read from
    /// <paramref name="dacpacStream"/> without executing anything against the target.
    /// </summary>
    Task<DeployResult> ScriptAsync(
        Stream dacpacStream,
        string connectionString,
        string? targetDatabaseName,
        IProgress<string>? progress,
        DeployOptions? options,
        CancellationToken cancellationToken = default);
}

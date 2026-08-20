using Npgsql;
using Squill.Core;
using Squill.Dacpac;

namespace Squill.Provider.Postgres;

/// <summary>
/// The result of a deploy: the SQL script that was generated from the diff, and whether it was
/// actually executed against the target database. On a dry run the script is generated and
/// returned but not run.
/// </summary>
/// <param name="Script">
/// The full SQL script generated from the diff between the DACPAC's model and the target
/// database's current model. Empty when the target already matches the DACPAC.
/// </param>
/// <param name="WasExecuted">
/// <c>true</c> if the script was run against the target database; <c>false</c> on a dry run.
/// </param>
public readonly record struct DeployResult(string Script, bool WasExecuted);

/// <summary>
/// Deploys a Squill-built PostgreSQL DACPAC to a target database. This is the shared deploy path
/// used by the console <c>deploy</c> verb (and other hosts), mirroring <see cref="DacpacBuilder"/>
/// on the build side. The deploy orchestration is provider-agnostic (see
/// <see cref="DacpacDeployerBase"/>); this type binds the PostgreSQL hooks and preserves the
/// provider's static entry points.
/// </summary>
public static class DacpacDeployer
{
    private static readonly Deployer Instance = new();

    /// <summary>
    /// Deploys the DACPAC at <paramref name="dacpacPath"/> to the database named by the
    /// connection string (or <paramref name="targetDatabaseName"/> when given).
    /// </summary>
    public static async Task<DeployResult> DeployFromFileAsync(
        string dacpacPath,
        string connectionString,
        string? targetDatabaseName = null,
        bool dryRun = false,
        IProgress<string>? progress = null,
        DeployOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(dacpacPath);

        return await DeployAsync(
            stream, connectionString, targetDatabaseName, dryRun, progress, options,
            cancellationToken);
    }

    /// <summary>
    /// Generates the deployment script that would bring the target database into the schema
    /// described by the DACPAC at <paramref name="dacpacPath"/>, without executing anything
    /// against the target. This backs the <c>squill script</c> verb (issue #21): the target is
    /// only read, so the connection needs no more than permission to view the schema.
    /// </summary>
    /// <remarks>
    /// This is the diff-and-script half of <see cref="DeployFromFileAsync"/> with the execute
    /// half omitted. Data-loss changes are <em>included</em> in the returned script (so it is a
    /// faithful preview of what a deploy would run); the
    /// <see cref="DeployOptions.BlockOnPossibleDataLoss"/> policy is a deploy-time concern and is
    /// not enforced here. The returned <see cref="DeployResult.WasExecuted"/> is always <c>false</c>.
    /// </remarks>
    public static async Task<DeployResult> ScriptFromFileAsync(
        string dacpacPath,
        string connectionString,
        string? targetDatabaseName = null,
        IProgress<string>? progress = null,
        DeployOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(dacpacPath);

        return await DeployAsync(
            stream, connectionString, targetDatabaseName, dryRun: true, progress, options,
            cancellationToken);
    }

    /// <summary>
    /// Deploys the DACPAC read from <paramref name="dacpacStream"/> to the target database. See
    /// <see cref="DeployFromFileAsync"/> for parameter semantics.
    /// </summary>
    public static async Task<DeployResult> DeployAsync(
        Stream dacpacStream,
        string connectionString,
        string? targetDatabaseName = null,
        bool dryRun = false,
        IProgress<string>? progress = null,
        DeployOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = await Instance.RunAsync(
            dacpacStream, connectionString, targetDatabaseName, dryRun, progress, options,
            cancellationToken);

        return new DeployResult(result.Script, result.WasExecuted);
    }

    // The PostgreSQL-specific deploy hooks. Instance-based so it can carry the base's
    // orchestration; the static surface above adapts it back to the provider's DeployResult.
    private sealed class Deployer : DacpacDeployerBase
    {
        public Task<Squill.Dacpac.DeployResult> RunAsync(
            Stream dacpacStream,
            string connectionString,
            string? targetDatabaseName,
            bool dryRun,
            IProgress<string>? progress,
            DeployOptions? options,
            CancellationToken cancellationToken)
            => DeployCoreAsync(
                dacpacStream, connectionString, targetDatabaseName, dryRun, progress, options,
                cancellationToken);

        protected override IDatabaseProvider CreateProvider(string connectionString)
            => new PostgresDatabaseProvider(connectionString);

        protected override IDatabase CreateDatabase(string connectionString, string databaseName)
            => new PostgresDatabase(connectionString, databaseName);

        protected override TargetVersion GetServerVersion(IDatabase database)
            => ((PostgresDatabase)database).GetServerVersion();

        protected override IScriptGenerator CreateScriptGenerator(ModelMetadata metadata)
            => new PostgresScriptGenerator();

        protected override string GetEngineName(ModelMetadata metadata) => "PostgreSQL";

        protected override string ResolveDatabaseName(string connectionString)
        {
            var database = new NpgsqlConnectionStringBuilder(connectionString).Database;

            if (string.IsNullOrEmpty(database))
            {
                throw new ArgumentException(
                    "The connection string does not specify a Database and no target database "
                    + "name was provided. Specify the target database via --target-database or "
                    + "the connection string's Database keyword.",
                    nameof(connectionString));
            }

            return database;
        }

        // Disable function-body validation for the deploy session. A function, view or trigger
        // body may reference another function (or an aggregate) created later in the same deploy
        // — Pagila's inventory_held_by_customer calls inventory_in_stock, for instance. Body
        // dependencies are not parsed, so the create order cannot always place a callee before
        // its caller; deferring body checks lets every object be created and relies on the fact
        // that by the end of the deploy all referenced objects exist. This is exactly how
        // pg_dump/pg_restore load an interdependent schema. It persists for the session because
        // RunScriptAsync reuses one connection.
        protected override Task PrepareSessionAsync(IDatabase database, CancellationToken cancellationToken)
            => database.RunScriptAsync("SET check_function_bodies = off;", cancellationToken: cancellationToken);
    }
}

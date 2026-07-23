using MySqlConnector;
using Squill.Core;
using Squill.Dacpac;

namespace Squill.Provider.MariaDb;

/// <summary>
/// The result of a deploy: the generated SQL script and whether it was executed. On a dry run
/// the script is generated and returned but not run.
/// </summary>
public readonly record struct DeployResult(string Script, bool WasExecuted);

/// <summary>
/// Deploys a Squill-built MariaDB DACPAC to a target database, mirroring the Postgres provider's
/// deploy path. The deploy orchestration is provider-agnostic (see
/// <see cref="DacpacDeployerBase"/>); this type binds the MariaDB/MySQL hooks and preserves the
/// provider's static entry points.
/// </summary>
public static class DacpacDeployer
{
    private static readonly Deployer Instance = new();

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

    // The MariaDB/MySQL-specific deploy hooks.
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
            => new MariaDbDatabaseProvider(connectionString);

        protected override IDatabase CreateDatabase(string connectionString, string databaseName)
            => new MariaDbDatabase(connectionString, databaseName);

        protected override int GetServerMajorVersion(IDatabase database)
            => ((MariaDbDatabase)database).GetServerMajorVersion();

        protected override IScriptGenerator CreateScriptGenerator()
            => new MariaDbScriptGenerator();

        // The engine name follows the DACPAC's provider name so a MySQL DACPAC says "MySQL",
        // not "MariaDB".
        protected override string GetEngineName(ModelMetadata metadata)
            => string.Equals(metadata.ProviderName, "MySql", StringComparison.OrdinalIgnoreCase)
                ? "MySQL"
                : "MariaDB";

        protected override string ResolveDatabaseName(string connectionString)
        {
            var database = new MySqlConnectionStringBuilder(connectionString).Database;

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
    }
}

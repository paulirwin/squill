using MySqlConnector;
using Squill.Core;
using Squill.Dacpac;

namespace Squill.Provider.MariaDb;

/// <summary>
/// The result of a deploy: the generated SQL script and whether it was executed. On a dry
/// run the script is generated and returned but not run.
/// </summary>
public readonly record struct DeployResult(string Script, bool WasExecuted);

/// <summary>
/// Deploys a Squill-built MariaDB DACPAC to a target database, mirroring the Postgres
/// provider's deploy path: deserialize the DACPAC to a model, extract the target database's
/// current model, diff the two, and script and run the changes.
/// </summary>
public static class DacpacDeployer
{
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
        progress?.Report("Reading DACPAC...");
        var (metadata, sourceModel) = await DacpacSerializer.Deserialize(dacpacStream, cancellationToken);

        var databaseName = targetDatabaseName ?? ResolveDatabaseName(connectionString);

        IDatabaseProvider provider = new MariaDbDatabaseProvider(connectionString);

        await using var targetDb = new MariaDbDatabase(connectionString, databaseName);

        progress?.Report($"Connecting to database '{databaseName}'...");
        await targetDb.ConnectAsync(cancellationToken);

        // Enforce the DACPAC's recorded target platform before doing any work: fail if the
        // server predates the version the DACPAC was built for (SSDT-style), so we never
        // deploy a newer-targeted package to an older engine.
        EnforceTargetVersion(metadata, targetDb.GetServerMajorVersion(), progress);

        progress?.Report("Extracting current schema from target database...");
        var modelBuilder = provider.CreateDatabaseModelBuilder(targetDb);
        var targetModel = await modelBuilder.ExtractModelAsync(cancellationToken);

        progress?.Report("Comparing schemas...");

        var compareOptions = options ?? DeployOptions.CreateDefault();
        var comparison = SchemaCompare.Compare(
            provider, sourceModel, targetModel,
            compareOptions with { BlockOnPossibleDataLoss = false });

        var generator = new MariaDbScriptGenerator();

        // The full script is the schema diff bracketed by the DACPAC's deploy scripts, so
        // `squill script` and a dry run preview exactly what a deploy would execute.
        var script = DeploymentScripts.Compose(
            metadata.PreDeployScript, generator.GenerateScript(comparison), metadata.PostDeployScript);

        if (dryRun)
        {
            if (comparison.CausesDataLoss)
            {
                foreach (var reason in comparison.DataLossReasons)
                {
                    progress?.Report($"WARNING (would block without --allow-data-loss): {reason}");
                }
            }

            return new DeployResult(script, WasExecuted: false);
        }

        if (compareOptions.BlockOnPossibleDataLoss)
        {
            comparison.ThrowIfDataLoss();
        }

        // The pre-deployment script runs before any schema change, and runs even when the
        // schema is already up to date: like SSDT, deploy scripts are part of every deploy,
        // not just ones that alter the schema (seeding an unchanged schema must still work).
        if (!string.IsNullOrWhiteSpace(metadata.PreDeployScript))
        {
            progress?.Report("Running pre-deployment script...");
            await targetDb.RunScriptAsync(metadata.PreDeployScript, cancellationToken: cancellationToken);
        }

        if (comparison.Deltas.Count == 0)
        {
            progress?.Report("Target database schema already matches the DACPAC; no schema changes to apply.");
        }

        foreach (var delta in comparison.Deltas)
        {
            progress?.Report(DescribeDelta(delta));

            var sql = generator.GenerateScriptForDelta(delta);
            await targetDb.RunScriptAsync(sql, cancellationToken: cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(metadata.PostDeployScript))
        {
            progress?.Report("Running post-deployment script...");
            await targetDb.RunScriptAsync(metadata.PostDeployScript, cancellationToken: cancellationToken);
        }

        return new DeployResult(script, WasExecuted: true);
    }

    /// <summary>
    /// Throws <see cref="TargetVersionMismatchException"/> when the DACPAC records a target
    /// major version newer than the connected server. A DACPAC with no recorded target version
    /// (<c>null</c>) is unconstrained and always allowed. The engine name in the message
    /// follows the DACPAC's provider name so a MySQL DACPAC says "MySQL", not "MariaDB".
    /// </summary>
    private static void EnforceTargetVersion(
        ModelMetadata metadata, int serverMajorVersion, IProgress<string>? progress)
    {
        if (metadata.TargetMajorVersion is not { } required)
        {
            return;
        }

        var engineName = string.Equals(metadata.ProviderName, "MySql", StringComparison.OrdinalIgnoreCase)
            ? "MySQL"
            : "MariaDB";

        if (serverMajorVersion < required)
        {
            throw new TargetVersionMismatchException(required, serverMajorVersion, engineName);
        }

        progress?.Report(
            $"Target server is {engineName} {serverMajorVersion}; DACPAC targets {required}+ (OK).");
    }

    private static string DescribeDelta(SchemaDelta delta)
    {
        switch (delta)
        {
            case CreateDelta create:
                return $"Creating {ElementTypeLabel(create.Element.Type)} {create.Element.Name ?? "(anonymous)"}";
            case AlterDelta alter:
                return $"Altering {ElementTypeLabel(alter.SourceElement.Type)} {alter.SourceElement.Name ?? "(anonymous)"}";
            case RebuildTableDelta rebuild:
                return $"Rebuilding table {rebuild.SourceElement.Name ?? "(anonymous)"} ({rebuild.Reason})";
            case DropDelta drop:
                return $"Dropping {ElementTypeLabel(drop.Element.Type)} {drop.Element.Name ?? "(anonymous)"}";
            default:
                return $"Applying {delta.GetType().Name}";
        }
    }

    private static string ElementTypeLabel(string elementType) => elementType switch
    {
        MariaDbElementTypes.SqlTable => "table",
        MariaDbElementTypes.SqlIndex => "index",
        _ => elementType,
    };

    private static string ResolveDatabaseName(string connectionString)
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

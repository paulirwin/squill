using Npgsql;
using Squill.Core;
using Squill.Dacpac;

namespace Squill.Provider.Postgres;

/// <summary>
/// The result of a deploy: the SQL script that was generated from the diff, and
/// whether it was actually executed against the target database. On a dry run the
/// script is generated and returned but not run.
/// </summary>
/// <param name="Script">
/// The full SQL script generated from the diff between the DACPAC's model and the
/// target database's current model. Empty when the target already matches the DACPAC.
/// </param>
/// <param name="WasExecuted">
/// <c>true</c> if the script was run against the target database; <c>false</c> on a
/// dry run.
/// </param>
public readonly record struct DeployResult(string Script, bool WasExecuted);

/// <summary>
/// Deploys a Squill-built PostgreSQL DACPAC to a target database. This is the shared
/// deploy path used by the console <c>deploy</c> verb (and, in future, other hosts),
/// mirroring <see cref="DacpacBuilder"/> on the build side: deserialize the DACPAC to
/// a model, extract the target database's current model, diff the two, and script and
/// run the changes.
/// </summary>
public static class DacpacDeployer
{
    /// <summary>
    /// Deploys the DACPAC at <paramref name="dacpacPath"/> to the database named by
    /// the connection string (or <paramref name="targetDatabaseName"/> when given).
    /// </summary>
    /// <param name="dacpacPath">Path to the <c>.dacpac</c> file to deploy.</param>
    /// <param name="connectionString">Npgsql connection string for the target server.</param>
    /// <param name="targetDatabaseName">
    /// The database to deploy into. When <c>null</c>, the <c>Database</c> from
    /// <paramref name="connectionString"/> is used.
    /// </param>
    /// <param name="dryRun">
    /// When <c>true</c>, the diff is scripted and returned but not executed.
    /// </param>
    /// <param name="progress">
    /// Optional sink for human-readable progress messages describing each step of the
    /// deploy (e.g. "Creating table public.customer") as it happens. Pass <c>null</c>
    /// for a silent deploy.
    /// </param>
    public static async Task<DeployResult> DeployFromFileAsync(
        string dacpacPath,
        string connectionString,
        string? targetDatabaseName = null,
        bool dryRun = false,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(dacpacPath);

        return await DeployAsync(
            stream, connectionString, targetDatabaseName, dryRun, progress, cancellationToken);
    }

    /// <summary>
    /// Deploys the DACPAC read from <paramref name="dacpacStream"/> to the target
    /// database. See <see cref="DeployFromFileAsync"/> for parameter semantics.
    /// </summary>
    public static async Task<DeployResult> DeployAsync(
        Stream dacpacStream,
        string connectionString,
        string? targetDatabaseName = null,
        bool dryRun = false,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report("Reading DACPAC...");
        var (_, sourceModel) = await DacpacSerializer.Deserialize(dacpacStream, cancellationToken);

        var databaseName = targetDatabaseName ?? ResolveDatabaseName(connectionString);

        IDatabaseProvider provider = new PostgresDatabaseProvider(connectionString);

        await using var targetDb = new PostgresDatabase(connectionString, databaseName);

        progress?.Report($"Connecting to database '{databaseName}'...");
        await targetDb.ConnectAsync(cancellationToken);

        progress?.Report("Extracting current schema from target database...");
        var modelBuilder = provider.CreateDatabaseModelBuilder(targetDb);
        var targetModel = await modelBuilder.ExtractModelAsync(cancellationToken);

        progress?.Report("Comparing schemas...");
        var comparison = SchemaCompare.Compare(provider, sourceModel, targetModel);

        var generator = new PostgresScriptGenerator();
        var script = generator.GenerateScript(comparison);

        if (dryRun)
        {
            return new DeployResult(script, WasExecuted: false);
        }

        if (comparison.Deltas.Count == 0)
        {
            progress?.Report("Target database already matches the DACPAC; nothing to deploy.");
            return new DeployResult(script, WasExecuted: true);
        }

        // Run the deltas one at a time (mirroring PostgresDatabase.PublishAsync) so each
        // step can be reported to the progress sink as it is applied, rather than running
        // one opaque batch.
        foreach (var delta in comparison.Deltas)
        {
            progress?.Report(DescribeDelta(delta));

            var sql = generator.GenerateScriptForDelta(delta);
            await targetDb.RunScriptAsync(sql, cancellationToken: cancellationToken);
        }

        return new DeployResult(script, WasExecuted: true);
    }

    /// <summary>
    /// A short, human-readable description of what applying <paramref name="delta"/>
    /// will do, for progress reporting — e.g. "Creating table public.customer".
    /// </summary>
    private static string DescribeDelta(SchemaDelta delta)
    {
        if (delta is CreateDelta create)
        {
            var label = ElementTypeLabel(create.Element.Type);
            var name = create.Element.Name ?? "(anonymous)";

            return $"Creating {label} {name}";
        }

        return $"Applying {delta.GetType().Name}";
    }

    private static string ElementTypeLabel(string elementType) => elementType switch
    {
        PostgresElementTypes.SqlTable => "table",
        PostgresElementTypes.SqlExtension => "extension",
        PostgresElementTypes.SqlIndex => "index",
        _ => elementType,
    };

    private static string ResolveDatabaseName(string connectionString)
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
}

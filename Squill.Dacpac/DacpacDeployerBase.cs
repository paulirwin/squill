using Squill.Core;

namespace Squill.Dacpac;

/// <summary>
/// The provider-agnostic deploy orchestration, mirroring the build side: deserialize the
/// DACPAC to a model, extract the target database's current model, diff the two, and script
/// and run the changes (or, on a dry run, just script them). Every step here is identical
/// across providers.
///
/// The engine-specific parts are hooks: creating the provider and a connected target database
/// (<see cref="CreateProvider"/> / <see cref="CreateDatabase"/> / <see cref="GetServerVersion"/>),
/// the script generator (<see cref="CreateScriptGenerator"/>), the engine name for version
/// messages (<see cref="GetEngineName"/>), an optional per-session setup before deltas run
/// (<see cref="PrepareSessionAsync"/>, a no-op by default), and parsing the target database
/// name out of the connection string (<see cref="ResolveDatabaseName"/>).
/// </summary>
public abstract class DacpacDeployerBase
{
    protected abstract IDatabaseProvider CreateProvider(string connectionString);

    protected abstract IDatabase CreateDatabase(string connectionString, string databaseName);

    /// <summary>
    /// The connected server's version — major, minor and patch. The components below the major
    /// matter because much of the MySQL and MariaDB DDL surface landed in point releases, so a
    /// DACPAC targeting 8.4 must be refused against an 8.0 server even though the majors match,
    /// and one targeting 8.0.13 against an 8.0.3 server even though the minors match too.
    /// </summary>
    protected abstract TargetVersion GetServerVersion(IDatabase database);

    protected abstract IScriptGenerator CreateScriptGenerator();

    /// <summary>The engine name used in version-enforcement messages (e.g. "PostgreSQL", "MySQL").</summary>
    protected abstract string GetEngineName(ModelMetadata metadata);

    /// <summary>Parses the target database name from the connection string.</summary>
    protected abstract string ResolveDatabaseName(string connectionString);

    /// <summary>
    /// Runs any per-session setup on <paramref name="database"/> before the deltas execute.
    /// The default is a no-op; Postgres overrides it to disable function-body validation.
    /// </summary>
    protected virtual Task PrepareSessionAsync(IDatabase database, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <summary>
    /// Deploys the DACPAC read from <paramref name="dacpacStream"/> to the target database.
    /// When <paramref name="dryRun"/> is <c>true</c>, the diff is scripted and returned but not
    /// executed.
    /// </summary>
    protected async Task<DeployResult> DeployCoreAsync(
        Stream dacpacStream,
        string connectionString,
        string? targetDatabaseName,
        bool dryRun,
        IProgress<string>? progress,
        DeployOptions? options,
        CancellationToken cancellationToken)
    {
        // The provider is created first because deserializing needs its identity rules: whether a
        // property takes part in its element's identity is not stored in the DACPAC (it has no
        // SSDT-compatible representation), so the provider restates it as the model is read.
        // Without it a domain's CHECK or a view's query would come back participating in the hash
        // and the object would appear changed on every deploy (issue #122).
        var provider = CreateProvider(connectionString);

        progress?.Report("Reading DACPAC...");
        var (metadata, sourceModel) = await DacpacSerializer.Deserialize(
            dacpacStream, provider.DependencyAnalyzer, cancellationToken);

        var databaseName = targetDatabaseName ?? ResolveDatabaseName(connectionString);

        await using var targetDb = CreateDatabase(connectionString, databaseName);

        progress?.Report($"Connecting to database '{databaseName}'...");
        await targetDb.ConnectAsync(cancellationToken);

        // Enforce the DACPAC's recorded target platform before doing any work: fail if the
        // server predates the version the DACPAC was built for (SSDT-style), so we never deploy
        // a newer-targeted package to an older engine.
        EnforceTargetVersion(metadata, GetServerVersion(targetDb), progress);

        progress?.Report("Extracting current schema from target database...");
        var modelBuilder = provider.CreateDatabaseModelBuilder(targetDb);
        var targetModel = await modelBuilder.ExtractModelAsync(cancellationToken);

        progress?.Report("Comparing schemas...");

        // Compare without enforcing the data-loss block, so the script (and its data-loss
        // reasons) can be computed even for a dry run. The block is enforced below, only for a
        // real run — a dry run must still be able to preview a destructive script.
        var compareOptions = options ?? DeployOptions.CreateDefault();
        var comparison = SchemaCompare.Compare(
            provider, sourceModel, targetModel,
            compareOptions with { BlockOnPossibleDataLoss = false });

        var generator = CreateScriptGenerator();

        // The full script is the schema diff bracketed by the DACPAC's deploy scripts, so
        // `squill script` and a dry run preview exactly what a deploy would execute.
        var script = DeploymentScripts.Compose(
            metadata.PreDeployScript, generator.GenerateScript(comparison), metadata.PostDeployScript);

        if (dryRun)
        {
            // Surface the data-loss reasons so a dry run reveals what would be blocked.
            if (comparison.CausesDataLoss)
            {
                foreach (var reason in comparison.DataLossReasons)
                {
                    progress?.Report($"WARNING (would block without --allow-data-loss): {reason}");
                }
            }

            return new DeployResult(script, WasExecuted: false);
        }

        // Enforce the block-on-possible-data-loss policy before executing anything.
        if (compareOptions.BlockOnPossibleDataLoss)
        {
            comparison.ThrowIfDataLoss();
        }

        await PrepareSessionAsync(targetDb, cancellationToken);

        // The pre-deployment script runs before any schema change, and runs even when the
        // schema is already up to date: like SSDT, deploy scripts are part of every deploy, not
        // just ones that alter the schema (seeding an unchanged schema must still work).
        if (!string.IsNullOrWhiteSpace(metadata.PreDeployScript))
        {
            progress?.Report("Running pre-deployment script...");
            await targetDb.RunScriptAsync(metadata.PreDeployScript, cancellationToken: cancellationToken);
        }

        if (comparison.Deltas.Count == 0)
        {
            progress?.Report("Target database schema already matches the DACPAC; no schema changes to apply.");
        }

        // Run the deltas one at a time so each step can be reported to the progress sink as it
        // is applied, rather than running one opaque batch.
        foreach (var delta in comparison.Deltas)
        {
            progress?.Report(DescribeDelta(delta));

            var sql = generator.GenerateScriptForDelta(delta);

            // A delta that renders to nothing has nothing to run, and both engines' drivers
            // reject an empty command outright rather than treating it as a no-op (issue #158).
            // Matching the pre/post-deploy scripts above, which are already skipped when empty.
            if (string.IsNullOrWhiteSpace(sql))
            {
                continue;
            }

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
    /// Throws <see cref="TargetVersionMismatchException"/> when the DACPAC's recorded target
    /// version is newer than the connected server. The target is a floor with no ceiling, so the
    /// test is one-sided: any server at or above it passes, however far above. A DACPAC with no
    /// recorded target version (<c>null</c>) is unconstrained and always allowed.
    ///
    /// <para>
    /// The comparison is on the whole version, not the major alone, which is what lets a floor of
    /// 8.4 be refused against an 8.0 server while a floor of 8.0 still deploys happily to 8.4.
    /// </para>
    /// </summary>
    protected void EnforceTargetVersion(
        ModelMetadata metadata, TargetVersion serverVersion, IProgress<string>? progress = null)
    {
        if (metadata.TargetVersion is not { } required)
        {
            return;
        }

        var engineName = GetEngineName(metadata);

        if (serverVersion < required)
        {
            throw new TargetVersionMismatchException(
                required.Major, required.Minor,
                serverVersion.Major, serverVersion.Minor,
                engineName,
                required.ToString(),
                serverVersion.ToString());
        }

        progress?.Report(
            $"Target server is {engineName} {serverVersion}; DACPAC targets {required}+ (OK).");
    }

    /// <summary>
    /// A short, human-readable description of what applying <paramref name="delta"/> will do,
    /// for progress reporting — e.g. "Creating table public.customer".
    /// </summary>
    private static string DescribeDelta(SchemaDelta delta) => delta switch
    {
        CreateDelta create =>
            $"Creating {ElementTypeLabel(create.Element.Type)} {create.Element.Name ?? "(anonymous)"}",
        AlterDelta alter =>
            $"Altering {ElementTypeLabel(alter.SourceElement.Type)} {alter.SourceElement.Name ?? "(anonymous)"}",
        RebuildTableDelta rebuild =>
            $"Rebuilding table {rebuild.SourceElement.Name ?? "(anonymous)"} ({rebuild.Reason})",
        DropDelta drop =>
            $"Dropping {ElementTypeLabel(drop.Element.Type)} {drop.Element.Name ?? "(anonymous)"}",
        _ => $"Applying {delta.GetType().Name}",
    };

    // The element types are shared vocabulary (SqlElementTypes), so one label map covers every
    // provider; a type with no friendly label falls back to its raw name.
    private static string ElementTypeLabel(string elementType) => elementType switch
    {
        SqlElementTypes.SqlTable => "table",
        SqlElementTypes.SqlIndex => "index",
        SqlElementTypes.SqlView => "view",
        SqlElementTypes.SqlProcedure => "procedure",
        SqlElementTypes.SqlFunction => "function",
        SqlElementTypes.SqlTrigger => "trigger",
        SqlElementTypes.SqlPrimaryKeyConstraint => "primary key",
        SqlElementTypes.SqlForeignKeyConstraint => "foreign key",
        // Postgres-only element types (string values, not referenced via the provider constants
        // to keep Squill.Dacpac free of provider references).
        "SqlExtension" => "extension",
        "SqlSchema" => "schema",
        "SqlAggregate" => "aggregate",
        "SqlEnumType" => "type",
        "SqlDomain" => "domain",
        "SqlSequence" => "sequence",
        "SqlCompositeType" => "type",
        "SqlRangeType" => "type",
        // MariaDB/MySQL-only: a scheduled event. PostgreSQL has no in-server scheduler.
        "SqlEvent" => "event",
        _ => elementType,
    };
}

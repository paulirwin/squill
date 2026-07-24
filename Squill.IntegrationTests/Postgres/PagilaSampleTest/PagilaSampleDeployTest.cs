using Squill.Core;
using Squill.Dacpac;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.PagilaSampleTest;

/// <summary>
/// Tests for the <c>PagilaSampleDatabase</c> sample — the PostgreSQL port of the Sakila
/// DVD-rental schema (see <c>samples/PagilaSampleDatabase</c>). The sample is a deliberately
/// ambitious, production-style schema (enum and domain types, identity keys, foreign keys,
/// multi-column and GiST indexes, tsvector full-text, array columns, PL/pgSQL and SQL functions,
/// an aggregate, and triggers).
///
/// <para>
/// As of issue #83 (CREATE TRIGGER) the whole sample builds and deploys against real Postgres:
/// enum/domain types (#75/#80), functions (#81), the group_concat aggregate (#82), tsvector +
/// GiST full-text and array columns (#76), and now triggers are all modeled. Deploy sequences
/// functions before the views and aggregates that use them, and disables function-body
/// validation for the session so a body may reference an object created later in the same
/// deploy (as pg_dump does).
/// </para>
/// </summary>
public class PagilaSampleDeployTest : PostgresIntegrationTestBase
{
    private const string SchemaResource =
        "Squill.IntegrationTests.Postgres.PagilaSampleTest.PagilaSchema.sql";

    private static async Task<string> WriteSchemaAsync(string dir, CancellationToken ct)
    {
        var schema = await new EmbeddedResourceFile(SchemaResource, FileKind.Compile)
            .ReadAllTextAsync(ct);
        var sqlPath = Path.Combine(dir, "Schema.sql");
        await File.WriteAllTextAsync(sqlPath, schema, ct);
        return sqlPath;
    }

    /// <summary>
    /// The full Pagila sample builds into a DACPAC — every feature it uses is now modeled. Needs
    /// no database.
    /// </summary>
    [Fact]
    public async Task BuildFullSchema_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-pagila-build");

        try
        {
            var sqlPath = await WriteSchemaAsync(tempDir.FullName, ct);
            var dacpacPath = Path.Combine(tempDir.FullName, "bin", "Pagila.dacpac");
            var workspace = DacpacBuilder.CreateWorkspace([sqlPath]);
            var metadata = new ModelMetadata { ProviderName = "Postgresql", Name = "Pagila" };

            await DacpacBuilder.BuildToFileAsync(workspace, metadata, dacpacPath, ct);

            Assert.True(File.Exists(dacpacPath));
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The end-to-end deploy of the Pagila sample into a real Postgres database via the exact code
    /// path <c>squill deploy</c> uses: build the DACPAC, deploy it, and assert it executed.
    /// </summary>
    [Fact]
    public async Task Deploy_PagilaSample_ProducesTheSampleSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-pagila-deploy");

        try
        {
            var sqlPath = await WriteSchemaAsync(tempDir.FullName, ct);
            var dacpacPath = Path.Combine(tempDir.FullName, "bin", "Pagila.dacpac");
            var workspace = DacpacBuilder.CreateWorkspace([sqlPath]);
            var metadata = new ModelMetadata { ProviderName = "Postgresql", Name = "Pagila" };
            await DacpacBuilder.BuildToFileAsync(workspace, metadata, dacpacPath, ct);

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_pagila_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, dryRun: false,
                    cancellationToken: ct);

                // The goal is a successful full build → deploy against real Postgres.
                Assert.True(result.WasExecuted);

                // A few representative objects across the schema's feature surface exist,
                // proving the deploy reached the end and created the harder-to-model objects.
                await AssertObjectExistsAsync(provider, targetDbName,
                    "SELECT to_regclass('public.film') IS NOT NULL", "film table", ct);
                await AssertObjectExistsAsync(provider, targetDbName,
                    "SELECT to_regclass('public.actor_info') IS NOT NULL", "actor_info view", ct);
                await AssertObjectExistsAsync(provider, targetDbName,
                    "SELECT EXISTS (SELECT 1 FROM pg_proc WHERE proname = 'group_concat' AND prokind = 'a')",
                    "group_concat aggregate", ct);
                await AssertObjectExistsAsync(provider, targetDbName,
                    "SELECT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'film_fulltext_trigger')",
                    "film_fulltext_trigger", ct);
            }
            finally
            {
                await createdDb.DropAsync(ct);
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Deploying the same DACPAC twice is a no-op the second time (issue #122). The first deploy
    /// creates the schema; the second compares the unchanged source model against the model
    /// extracted from the database it just created, so every element must hash-match and the
    /// deploy must produce no deltas. This is the strongest available check that the parser
    /// builder and the database builder agree on every construct the sample uses — any facet one
    /// side records and the other does not shows up here as a spurious delta (or, for an object
    /// kind that cannot be altered in place, as a hard failure).
    /// </summary>
    [Fact]
    public async Task Deploy_PagilaSampleTwice_SecondDeployHasNoChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-pagila-redeploy");

        try
        {
            var sqlPath = await WriteSchemaAsync(tempDir.FullName, ct);
            var dacpacPath = Path.Combine(tempDir.FullName, "bin", "Pagila.dacpac");
            var workspace = DacpacBuilder.CreateWorkspace([sqlPath]);
            var metadata = new ModelMetadata { ProviderName = "Postgresql", Name = "Pagila" };
            await DacpacBuilder.BuildToFileAsync(workspace, metadata, dacpacPath, ct);

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_pagila_redeploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var first = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, dryRun: false,
                    cancellationToken: ct);
                Assert.True(first.WasExecuted);

                // The second deploy of an unchanged DACPAC must find nothing to do.
                var second = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, dryRun: false,
                    cancellationToken: ct);

                Assert.True(
                    string.IsNullOrWhiteSpace(second.Script),
                    $"expected no changes on redeploy, but got script:\n{second.Script}");
            }
            finally
            {
                await createdDb.DropAsync(ct);
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    private async Task AssertObjectExistsAsync(
        IDatabaseProvider provider, string dbName, string existsQuery, string what, CancellationToken ct)
    {
        var db = new PostgresDatabase(ConnectionString, dbName);
        await db.ConnectAsync(ct);

        await using var reader = await db.RunScriptReaderAsync(existsQuery, cancellationToken: ct);
        Assert.True(await reader.ReadAsync(ct), $"existence query for {what} returned no rows");
        Assert.True(reader.GetBoolean(0), $"expected {what} to exist after deploy");
    }
}

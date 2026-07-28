using MySqlConnector;
using Squill.Core;
using Squill.Dacpac;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb.SakilaSample;

/// <summary>
/// Tests for the <c>SakilaSampleDatabase</c> sample — the classic Sakila DVD-rental schema for
/// MariaDB / MySQL (see <c>samples/SakilaSampleDatabase</c>). The sample is a real, non-trivial
/// production-style schema: 16 tables with AUTO_INCREMENT keys, foreign keys (including the
/// circular staff&lt;-&gt;store pair), ENUM / SET / YEAR columns, ON UPDATE CURRENT_TIMESTAMP, a
/// FULLTEXT index, six views, stored procedures, three stored functions, and triggers.
///
/// <para>
/// As of issue #100 (CREATE TRIGGER) the whole sample builds and deploys against a real MariaDB
/// / MySQL container: the enum/set value lists are preserved in the generated DDL (issue #73),
/// the three stored functions (<c>get_customer_balance</c>, <c>inventory_in_stock</c>,
/// <c>inventory_held_by_customer</c>) are modeled alongside the procedures (issue #74), and the
/// three <c>film</c> triggers (<c>del_film</c>, <c>ins_film</c>, <c>upd_film</c>) that keep the
/// FULLTEXT-indexed <c>film_text</c> copy in sync are modeled too. Routines and triggers are
/// sequenced after the tables and views they read, so the deploy applies in one pass. The
/// assertions below spot-check a table, enum column, view, function, procedure, and trigger.
/// </para>
///
/// <para>
/// This mirrors the Postgres <c>PagilaSampleDeployTest</c>: a DB-less
/// <see cref="BuildFullSchema_ProducesADacpac"/> and an end-to-end
/// <see cref="Deploy_SakilaSample_ProducesTheSampleSchema"/> that deploys the full schema and
/// spot-checks a representative object from each feature area.
/// </para>
/// </summary>
public abstract class SakilaSampleDeployTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    // The full Sakila schema: all tables, views, procedures, functions, and triggers.
    private const string SchemaResource =
        "Squill.IntegrationTests.MariaDb.SakilaSample.SakilaSchema.sql";

    private async Task<string> BuildDacpacAsync(string directory, CancellationToken ct)
    {
        var schema = await new EmbeddedResourceFile(SchemaResource, FileKind.Compile)
            .ReadAllTextAsync(ct);

        return await DacpacTestBuilder.BuildToFileAsync(
            directory,
            schema,
            Fixture.ProviderName,
            ws => new ParserWorkspaceModelBuilder(ws, new Squill.MariaDbParser.AntlrMariaDbParser(), Fixture.EngineOf()),
            ct,
            name: "Sakila");
    }

    /// <summary>
    /// The full Sakila sample builds into a DACPAC — every feature it uses is now modeled. This
    /// proves the sample's declarative SQL parses and serialises through the real build path
    /// (the same path <c>squill</c>'s SDK uses). Needs no database.
    /// </summary>
    [Fact]
    public async Task BuildFullSchema_ProducesADacpac()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-sakila-build");

        try
        {
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, ct);

            Assert.True(File.Exists(dacpacPath), "The build should have produced a .dacpac file.");
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    /// <summary>
    /// The end-to-end deploy of the full Sakila sample into a real MariaDB / MySQL database via
    /// the exact code path <c>squill deploy</c> uses: build the DACPAC, deploy it, and assert it
    /// executed and created a representative object from each feature area.
    /// </summary>
    [Fact]
    public async Task Deploy_SakilaSample_ProducesTheSampleSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-sakila-deploy");

        try
        {
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, ct);

            IDatabaseProvider provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
            var targetDbName = $"squill_sakila_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, Fixture.ConnectionString, targetDbName, dryRun: false,
                    cancellationToken: ct);

                Assert.True(result.WasExecuted, $"[{Fixture.EngineName}] deploy should execute.");

                // A representative object from each feature area exists, proving the deploy
                // reached the end and created the harder-to-model objects — notably the film
                // table's enum/set columns (#73) and the three stored functions (#74).
                await AssertObjectExistsAsync(targetDbName,
                    "SELECT COUNT(*) FROM information_schema.TABLES "
                    + "WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'film'",
                    "film table", ct);
                await AssertObjectExistsAsync(targetDbName,
                    "SELECT COUNT(*) FROM information_schema.COLUMNS "
                    + "WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'film' AND COLUMN_NAME = 'rating' "
                    + "AND DATA_TYPE = 'enum'",
                    "film.rating enum column", ct);
                await AssertObjectExistsAsync(targetDbName,
                    "SELECT COUNT(*) FROM information_schema.VIEWS "
                    + "WHERE TABLE_SCHEMA = @db AND TABLE_NAME = 'customer_list'",
                    "customer_list view", ct);
                await AssertObjectExistsAsync(targetDbName,
                    "SELECT COUNT(*) FROM information_schema.ROUTINES "
                    + "WHERE ROUTINE_SCHEMA = @db AND ROUTINE_TYPE = 'FUNCTION' "
                    + "AND ROUTINE_NAME = 'get_customer_balance'",
                    "get_customer_balance function", ct);
                await AssertObjectExistsAsync(targetDbName,
                    "SELECT COUNT(*) FROM information_schema.ROUTINES "
                    + "WHERE ROUTINE_SCHEMA = @db AND ROUTINE_TYPE = 'PROCEDURE' "
                    + "AND ROUTINE_NAME = 'film_in_stock'",
                    "film_in_stock procedure", ct);
                await AssertObjectExistsAsync(targetDbName,
                    "SELECT COUNT(*) FROM information_schema.TRIGGERS "
                    + "WHERE TRIGGER_SCHEMA = @db AND TRIGGER_NAME = 'ins_film' "
                    + "AND EVENT_OBJECT_TABLE = 'film'",
                    "ins_film trigger", ct);
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
    /// deploy must produce no deltas.
    ///
    /// <para>
    /// This is the strongest available check that the parser builder and the database builder
    /// agree on every construct the sample uses — any facet one side records and the other does
    /// not shows up here as a spurious delta. It matters most for the six views: a view's query
    /// is excluded from its identity precisely because MariaDB/MySQL rewrite it when they store
    /// it, and that exclusion has to survive the DACPAC round trip for a redeploy to be clean.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Deploy_SakilaSampleTwice_SecondDeployHasNoChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-sakila-redeploy");

        try
        {
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, ct);

            IDatabaseProvider provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
            var targetDbName = $"squill_sakila_redeploy_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var first = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, Fixture.ConnectionString, targetDbName, dryRun: false,
                    cancellationToken: ct);
                Assert.True(first.WasExecuted, $"[{Fixture.EngineName}] first deploy should execute.");

                // The second deploy of an unchanged DACPAC must find nothing to do.
                var second = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, Fixture.ConnectionString, targetDbName, dryRun: false,
                    cancellationToken: ct);

                Assert.True(
                    string.IsNullOrWhiteSpace(second.Script),
                    $"[{Fixture.EngineName}] expected no changes on redeploy, but got script:\n{second.Script}");
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

    // Runs a COUNT(*) existence query (parameterized on the target database name) and asserts
    // it returned a positive count, i.e. the object was created by the deploy.
    private async Task AssertObjectExistsAsync(
        string databaseName, string countQuery, string what, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(
            new MySqlConnectionStringBuilder(Fixture.ConnectionString) { Database = databaseName }
                .ConnectionString);
        await connection.OpenAsync(ct);

        await using var command = new MySqlCommand(countQuery, connection);
        command.Parameters.AddWithValue("@db", databaseName);

        var count = Convert.ToInt64(await command.ExecuteScalarAsync(ct));

        Assert.True(count > 0, $"[{Fixture.EngineName}] expected {what} to exist after deploy.");
    }
}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class SakilaSampleDeployTestsMariaDb(MariaDbFixture fixture)
    : SakilaSampleDeployTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class SakilaSampleDeployTestsMySql(MySqlFixture fixture)
    : SakilaSampleDeployTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

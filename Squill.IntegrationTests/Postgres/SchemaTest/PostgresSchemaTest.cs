using Npgsql;
using Squill.Core;
using Squill.Dacpac;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.SchemaTest;

// End-to-end coverage for user-defined schema support (issue #37) against real Postgres:
// a declared CREATE SCHEMA plus a table (and index) in that schema deploys correctly,
// the deployed model round-trips (parser model == extracted model), and dropping the
// schema's objects works — proving schema-qualified DDL and schema-aware identity hold
// up against a real database.
public class PostgresSchemaTest : PostgresIntegrationTestBase
{
    [Fact]
    public async Task DeploySchemaAndTable_CreatesInDeclaredSchema_AndRoundTrips()
    {
        const string schema = """
CREATE SCHEMA staging;

CREATE TABLE staging.event
(
    event_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name     varchar(200) NOT NULL
);

CREATE INDEX ix_event_name ON staging.event (name);
""";

        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-schema-integration");

        try
        {
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, schema, ct);

            Model dacpacModel;
            await using (var stream = File.OpenRead(dacpacPath))
            {
                (_, dacpacModel) = await DacpacSerializer.Deserialize(stream, ct);
            }

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_schema_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, cancellationToken: ct);

                Assert.True(result.WasExecuted);
                // The table must be created in the declared schema, qualified.
                Assert.Contains("CREATE SCHEMA IF NOT EXISTS \"staging\"", result.Script);
                Assert.Contains("CREATE TABLE \"staging\".\"event\"", result.Script);

                // The table really landed in 'staging', not 'public'.
                await using var conn = await OpenAsync(targetDbName, ct);
                var tableSchema = await ScalarAsync(conn, """
SELECT table_schema FROM information_schema.tables WHERE table_name = 'event';
""");
                Assert.Equal("staging", tableSchema);

                // Data works against the qualified table.
                await ExecuteAsync(conn, "INSERT INTO staging.event (name) VALUES ('launch');", ct);
                var count = await ScalarAsync(conn, "SELECT count(*) FROM staging.event;");
                Assert.Equal(1L, count);

                // The deployed model must match the DACPAC's model — schema-qualified names,
                // the schema element, and the schema-aware index all round-trip.
                var deployedModel = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);

                Assert.Equal(
                    ElementHashMultiset(dacpacModel),
                    ElementHashMultiset(deployedModel));

                // The schema is represented as a first-class element on both sides.
                Assert.Contains(deployedModel.Elements,
                    e => e.Type == PostgresElementTypes.SqlSchema && e.Name == "staging");
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

    [Fact]
    public async Task RedeployingSameSchema_IsIdempotent_NoChanges()
    {
        const string schema = """
CREATE SCHEMA staging;
CREATE TABLE staging.event (event_id integer PRIMARY KEY, name varchar(200) NOT NULL);
""";

        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-schema-idempotent");

        try
        {
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, schema, ct);

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_schema_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, cancellationToken: ct);

                // A second deploy of the same schema must find nothing to do — proving the
                // parsed model and the extracted model of a non-public schema agree.
                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, cancellationToken: ct);

                Assert.True(string.IsNullOrEmpty(result.Script),
                    "Redeploying an unchanged non-public schema should produce no script.");
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

    [Fact]
    public async Task DeployNonPublicForeignKey_ResolvesAcrossSchema()
    {
        // A foreign key between two tables in a non-public schema must deploy: the
        // REFERENCES target has to be schema-qualified, or it fails to resolve.
        const string schema = """
CREATE SCHEMA app;

CREATE TABLE app.parent (id integer PRIMARY KEY);

CREATE TABLE app.child
(
    id        integer PRIMARY KEY,
    parent_id integer NOT NULL REFERENCES app.parent (id)
);
""";

        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-schema-fk");

        try
        {
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, schema, ct);

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_schema_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var result = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, cancellationToken: ct);

                Assert.True(result.WasExecuted);
                Assert.Contains("REFERENCES \"app\".\"parent\"", result.Script);

                // The FK really exists and enforces: inserting a child with a missing
                // parent must be rejected, proving the constraint bound the right table.
                await using var conn = await OpenAsync(targetDbName, ct);
                await ExecuteAsync(conn, "INSERT INTO app.parent (id) VALUES (1);", ct);
                await ExecuteAsync(conn, "INSERT INTO app.child (id, parent_id) VALUES (1, 1);", ct);

                var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteAsync(conn, "INSERT INTO app.child (id, parent_id) VALUES (2, 999);", ct));
                Assert.Equal("23503", ex.SqlState); // foreign_key_violation
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

    [Fact]
    public async Task CrossSchemaForeignKey_RoundTrips()
    {
        // A non-public table with a foreign key back to a public table (as in the sample's
        // audit.book_change -> public.book). The referenced-table name must round-trip so a
        // redeploy is a no-op — this was the case that regressed schema support.
        const string schema = """
CREATE SCHEMA audit;

CREATE TABLE record (record_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY);

CREATE TABLE audit.record_change
(
    record_change_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    record_id        integer NOT NULL REFERENCES public.record (record_id)
);
""";

        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-crossfk");

        try
        {
            var dacpacPath = await BuildDacpacAsync(tempDir.FullName, schema, ct);

            Model dacpacModel;
            await using (var stream = File.OpenRead(dacpacPath))
            {
                (_, dacpacModel) = await DacpacSerializer.Deserialize(stream, ct);
            }

            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_schema_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, cancellationToken: ct);

                var deployed = await provider.CreateDatabaseModelBuilder(createdDb).ExtractModelAsync(ct);
                Assert.Equal(ElementHashMultiset(dacpacModel), ElementHashMultiset(deployed));

                // A second deploy must find nothing to do — proving the cross-schema FK
                // round-trips (parser model == extracted model).
                var redeploy = await DacpacDeployer.DeployFromFileAsync(
                    dacpacPath, ConnectionString, targetDbName, cancellationToken: ct);
                Assert.True(string.IsNullOrEmpty(redeploy.Script),
                    "Redeploying an unchanged cross-schema FK should produce no script.");
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

    private static async Task<string> BuildDacpacAsync(string dir, string schema, CancellationToken ct)
    {
        var sqlPath = Path.Combine(dir, "Schema.sql");
        await File.WriteAllTextAsync(sqlPath, schema, ct);

        var dacpacPath = Path.Combine(dir, "bin", "TestDb.dacpac");
        var workspace = DacpacBuilder.CreateWorkspace([sqlPath]);
        var metadata = new ModelMetadata { ProviderName = "Postgresql", Name = "TestDb" };
        await DacpacBuilder.BuildToFileAsync(workspace, metadata, dacpacPath, ct);

        return dacpacPath;
    }

    private async Task<NpgsqlConnection> OpenAsync(string databaseName, CancellationToken ct)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = databaseName
        }.ConnectionString;

        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }

    private static List<string> ElementHashMultiset(Model model)
        => model.Elements
            .Select(e => Convert.ToHexString(e.Hash))
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();
}

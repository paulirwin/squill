using Npgsql;
using Squill.Core;
using Squill.Dacpac;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.ColumnDefaultTest;

// End-to-end coverage for column DEFAULT values (issue #36) against real Postgres.
//
// The load-bearing check is the round trip: a model built from SQL by the parser must,
// after publish, hash-match the model extracted from the database. Postgres canonicalizes
// stored defaults inconsistently (0 but '-5'::integer, 'active'::character varying), so
// this proves PostgresDefaultValue reduces both the parsed expression and the database's
// column_default text to the same canonical token. Also verifies the default actually
// applies on INSERT and that SET/DROP DEFAULT deploy correctly.
public class PostgresColumnDefaultTest : PostgresIntegrationTestBase
{
    [Fact]
    public async Task ColumnDefaults_RoundTrip_ModelHashesMatchAfterPublish()
    {
        const string schema = """
CREATE TABLE settings
(
    id       integer PRIMARY KEY,
    count    integer NOT NULL DEFAULT 0,
    status   varchar(20) NOT NULL DEFAULT 'active',
    enabled  boolean NOT NULL DEFAULT true,
    ratio    numeric(6, 2) NOT NULL DEFAULT 1.50
);
""";

        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        // Build the source model with the parser (no database), so the round trip proves
        // the parser and database builders agree on the canonical default form.
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Settings.sql", FileKind.Compile, schema));
        var model = await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(ct);

        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(ct);
            var comparison = SchemaCompare.Compare(provider, model, emptyModel);
            await testDb.PublishAsync(comparison, ct);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(ct);

            Assert.True(
                HashUtility.HashesEqual(model.Hash, publishedModel.Hash),
                "Parsed and extracted model hashes do not match — a default did not "
                + "canonicalize identically on both sides.");

            // The defaults must actually apply on INSERT.
            await using var conn = await OpenAsync(testDb.Name, ct);
            await ExecuteAsync(conn, "INSERT INTO settings (id) VALUES (1);", ct);

            Assert.Equal(0, await ScalarAsync(conn, "SELECT count FROM settings WHERE id = 1;"));
            Assert.Equal("active", await ScalarAsync(conn, "SELECT status FROM settings WHERE id = 1;"));
            Assert.Equal(true, await ScalarAsync(conn, "SELECT enabled FROM settings WHERE id = 1;"));
            Assert.Equal(1.50m, await ScalarAsync(conn, "SELECT ratio FROM settings WHERE id = 1;"));
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    [Fact]
    public async Task ChangeDefault_AltersInPlace_AndAppliesToNewRows()
    {
        const string before = """
CREATE TABLE orders
(
    id     integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    status varchar(20) NOT NULL DEFAULT 'active'
);
""";
        // The default changes, and a second column gains a default.
        const string after = """
CREATE TABLE orders
(
    id       integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    status   varchar(20) NOT NULL DEFAULT 'pending'
);
""";

        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-default-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_default_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var beforeDacpac = await BuildDacpacAsync(tempDir.FullName, "before", before, ct);
                await DacpacDeployer.DeployFromFileAsync(
                    beforeDacpac, ConnectionString, targetDbName, cancellationToken: ct);

                // A row inserted under the old default keeps its value.
                await using (var seedConn = await OpenAsync(targetDbName, ct))
                {
                    await ExecuteAsync(seedConn, "INSERT INTO orders DEFAULT VALUES;", ct);
                }

                var afterDacpac = await BuildDacpacAsync(tempDir.FullName, "after", after, ct);
                var result = await DacpacDeployer.DeployFromFileAsync(
                    afterDacpac, ConnectionString, targetDbName, cancellationToken: ct);

                Assert.True(result.WasExecuted);
                Assert.Contains("SET DEFAULT 'pending'", result.Script);

                // The database model must match the changed DACPAC's model.
                Model afterModel;
                await using (var stream = File.OpenRead(afterDacpac))
                {
                    (_, afterModel) = await DacpacSerializer.Deserialize(stream, ct);
                }

                var deployedModel = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);

                Assert.Equal(ElementHashMultiset(afterModel), ElementHashMultiset(deployedModel));

                await using var conn = await OpenAsync(targetDbName, ct);

                // The pre-existing row's value is unchanged (a default change is not a data
                // rewrite).
                var existing = await ScalarAsync(
                    conn, "SELECT status FROM orders ORDER BY id LIMIT 1;");
                Assert.Equal("active", existing);

                // A new row picks up the new default.
                await ExecuteAsync(conn, "INSERT INTO orders DEFAULT VALUES;", ct);
                var newest = await ScalarAsync(
                    conn, "SELECT status FROM orders ORDER BY id DESC LIMIT 1;");
                Assert.Equal("pending", newest);
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

    private static async Task<string> BuildDacpacAsync(
        string dir, string label, string schema, CancellationToken ct)
    {
        var sqlPath = Path.Combine(dir, $"{label}.sql");
        await File.WriteAllTextAsync(sqlPath, schema, ct);

        var dacpacPath = Path.Combine(dir, "bin", $"{label}.dacpac");
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

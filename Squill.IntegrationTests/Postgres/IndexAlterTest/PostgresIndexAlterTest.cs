using Npgsql;
using Squill.Core;
using Squill.Dacpac;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.IndexAlterTest;

// End-to-end coverage for changing a standalone index on an otherwise-unchanged table
// (issue #36) against real Postgres: deploy an initial schema, seed data, then deploy a
// changed index definition to the same database via the exact DacpacDeployer code path
// the CLI uses. An index carries no data, so the change is a drop-and-recreate that must
// leave the table's rows untouched and land the new index shape in the database.
public class PostgresIndexAlterTest : PostgresIntegrationTestBase
{
    [Fact]
    public async Task ChangeIndexColumns_RecreatesIndex_AndPreservesData()
    {
        const string before = """
CREATE TABLE film
(
    film_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title   varchar(255) NOT NULL,
    rating  varchar(10)
);

CREATE INDEX idx_film_title ON film (title);
""";
        // The index gains a second column; the table itself is unchanged.
        const string after = """
CREATE TABLE film
(
    film_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title   varchar(255) NOT NULL,
    rating  varchar(10)
);

CREATE INDEX idx_film_title ON film (title, rating);
""";

        await RunDeployScenarioAsync(
            before, after,
            seedSql: "INSERT INTO film (title, rating) VALUES ('Alpha', 'PG'), ('Beta', 'R');",
            assertAfterAsync: async conn =>
            {
                // The rows must survive the index recreate.
                var count = await ScalarAsync(conn, "SELECT count(*) FROM film;");
                Assert.Equal(2L, count);

                // The index must now cover both columns.
                var indexDef = (string?)await ScalarAsync(conn, """
SELECT indexdef FROM pg_indexes
WHERE tablename = 'film' AND indexname = 'idx_film_title';
""");
                Assert.NotNull(indexDef);
                Assert.Contains("title", indexDef);
                Assert.Contains("rating", indexDef);
            });
    }

    [Fact]
    public async Task ChangeIndexUniqueness_RecreatesIndex()
    {
        const string before = """
CREATE TABLE account
(
    account_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email      varchar(255) NOT NULL
);

CREATE INDEX idx_account_email ON account (email);
""";
        const string after = """
CREATE TABLE account
(
    account_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email      varchar(255) NOT NULL
);

CREATE UNIQUE INDEX idx_account_email ON account (email);
""";

        await RunDeployScenarioAsync(
            before, after,
            seedSql: "INSERT INTO account (email) VALUES ('a@example.com');",
            assertAfterAsync: async conn =>
            {
                // The recreated index must be unique.
                var isUnique = await ScalarAsync(conn, """
SELECT indisunique FROM pg_index
JOIN pg_class ON pg_class.oid = pg_index.indexrelid
WHERE pg_class.relname = 'idx_account_email';
""");
                Assert.Equal(true, isUnique);

                // And it must actually enforce uniqueness now.
                await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                    conn, "INSERT INTO account (email) VALUES ('a@example.com');",
                    TestContext.Current.CancellationToken));
            });
    }

    // Deploys `before`, seeds data, deploys `after` to the same database, then runs the
    // caller's assertions against the final database. Verifies the database's model
    // matches the `after` DACPAC's model along the way.
    private async Task RunDeployScenarioAsync(
        string before,
        string after,
        string seedSql,
        Func<NpgsqlConnection, Task> assertAfterAsync,
        DeployOptions? afterOptions = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-index-alter-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_index_alter_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                // Deploy the initial schema.
                var beforeDacpac = await BuildDacpacAsync(tempDir.FullName, "before", before, ct);
                await DacpacDeployer.DeployFromFileAsync(
                    beforeDacpac, ConnectionString, targetDbName, cancellationToken: ct);

                // Seed data so we can prove it survives the index recreate.
                await using (var seedConn = await OpenAsync(targetDbName, ct))
                {
                    await ExecuteAsync(seedConn, seedSql, ct);
                }

                // Deploy the changed schema to the same database.
                var afterDacpac = await BuildDacpacAsync(tempDir.FullName, "after", after, ct);
                var result = await DacpacDeployer.DeployFromFileAsync(
                    afterDacpac, ConnectionString, targetDbName, options: afterOptions,
                    cancellationToken: ct);

                Assert.True(result.WasExecuted);
                Assert.False(string.IsNullOrWhiteSpace(result.Script),
                    "An index change should generate a non-empty script.");

                // The database's model must now match the changed DACPAC's model.
                Model afterModel;
                await using (var stream = File.OpenRead(afterDacpac))
                {
                    (_, afterModel) = await DacpacSerializer.Deserialize(stream, ct);
                }

                var deployedModel = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);

                Assert.Equal(
                    ModelAssertions.ElementHashMultiset(afterModel),
                    ModelAssertions.ElementHashMultiset(deployedModel));

                // Run the scenario-specific data assertions.
                await using var conn = await OpenAsync(targetDbName, ct);
                await assertAfterAsync(conn);
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

    private static Task<string> BuildDacpacAsync(
        string dir, string label, string schema, CancellationToken ct)
        => DacpacTestBuilder.BuildToFileAsync(
            dir, schema, "Postgresql",
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            ct, label: label, outputSubdirectory: "bin", fileName: label);

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
}

using Npgsql;
using Squill.Core;
using Squill.Dacpac;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.AlterTableTest;

// End-to-end ALTER / table-rebuild coverage (issues #29, #32) against real Postgres:
// deploy an initial schema, then deploy a changed schema to the same database via the
// exact DacpacDeployer code path the CLI uses, and assert the database ends up matching
// the changed DACPAC — and that existing row data survives an in-place ALTER and a
// table rebuild.
public class PostgresAlterTableTest : PostgresIntegrationTestBase
{
    [Fact]
    public async Task AddColumn_AltersTableInPlace_AndPreservesData()
    {
        const string before = """
CREATE TABLE customer
(
    customer_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email       varchar(320) NOT NULL
);
""";
        const string after = """
CREATE TABLE customer
(
    customer_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email       varchar(320) NOT NULL,
    full_name   varchar(200) NULL
);
""";

        await RunDeployScenarioAsync(
            before, after,
            seedSql: "INSERT INTO customer (email) VALUES ('a@example.com'), ('b@example.com');",
            assertAfterAsync: async conn =>
            {
                // The pre-existing rows must survive an in-place ADD COLUMN.
                var count = await ScalarAsync(conn, "SELECT count(*) FROM customer;");
                Assert.Equal(2L, count);

                // The new column exists and is null for the old rows.
                var nulls = await ScalarAsync(
                    conn, "SELECT count(*) FROM customer WHERE full_name IS NULL;");
                Assert.Equal(2L, nulls);
            });
    }

    [Fact]
    public async Task WidenColumnType_AltersTableInPlace_AndPreservesData()
    {
        const string before = """
CREATE TABLE customer
(
    customer_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email       varchar(50) NOT NULL
);
""";
        const string after = """
CREATE TABLE customer
(
    customer_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email       varchar(320) NOT NULL
);
""";

        await RunDeployScenarioAsync(
            before, after,
            seedSql: "INSERT INTO customer (email) VALUES ('a@example.com');",
            assertAfterAsync: async conn =>
            {
                var email = await ScalarAsync(conn, "SELECT email FROM customer LIMIT 1;");
                Assert.Equal("a@example.com", email);
            });
    }

    [Fact]
    public async Task InsertColumnBetweenExisting_RebuildsTable_AndPreservesData()
    {
        const string before = """
CREATE TABLE customer
(
    customer_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email       varchar(320) NOT NULL
);
""";
        // full_name is inserted between customer_id and email, so the physical column
        // order changes and the table must be rebuilt.
        const string after = """
CREATE TABLE customer
(
    customer_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    full_name   varchar(200) NULL,
    email       varchar(320) NOT NULL
);
""";

        await RunDeployScenarioAsync(
            before, after,
            seedSql: "INSERT INTO customer (email) VALUES ('a@example.com'), ('b@example.com');",
            assertAfterAsync: async conn =>
            {
                // Data must survive the rebuild: both rows, with their emails, and the new
                // column present but null.
                var count = await ScalarAsync(conn, "SELECT count(*) FROM customer;");
                Assert.Equal(2L, count);

                var emails = await ScalarAsync(
                    conn, "SELECT count(*) FROM customer WHERE email LIKE '%@example.com';");
                Assert.Equal(2L, emails);

                var nulls = await ScalarAsync(
                    conn, "SELECT count(*) FROM customer WHERE full_name IS NULL;");
                Assert.Equal(2L, nulls);

                // The rebuilt table must have the new physical column order.
                var secondColumn = await ScalarAsync(conn, """
SELECT column_name FROM information_schema.columns
WHERE table_name = 'customer' AND ordinal_position = 2;
""");
                Assert.Equal("full_name", secondColumn);

                // The identity sequence must have been advanced past the copied values, so
                // a fresh insert gets a new key rather than colliding with a copied row's
                // customer_id (the rebuild copies identity values with OVERRIDING SYSTEM
                // VALUE, which alone would leave the sequence at 1).
                await ExecuteAsync(
                    conn, "INSERT INTO customer (email) VALUES ('c@example.com');",
                    TestContext.Current.CancellationToken);

                var newId = await ScalarAsync(
                    conn, "SELECT customer_id FROM customer WHERE email = 'c@example.com';");
                Assert.Equal(3, newId);

                var total = await ScalarAsync(conn, "SELECT count(*) FROM customer;");
                Assert.Equal(3L, total);
            });
        // No options override: this rebuild only inserts a column (drops none), so it is
        // lossless and must deploy under the default data-loss guard.
    }

    [Fact]
    public async Task RebuildReferencedTable_DropsAndRecreatesInboundForeignKey_AndPreservesData()
    {
        // customer is referenced by orders' FK. Rebuilding customer (inserting a column
        // mid-table) must drop that FK, rebuild, and recreate it — all in one transaction —
        // preserving both tables' data and leaving the FK enforcing again.
        const string before = """
CREATE TABLE customer
(
    customer_id integer PRIMARY KEY,
    email       varchar(320) NOT NULL
);

CREATE TABLE orders
(
    order_id    integer PRIMARY KEY,
    customer_id integer NOT NULL REFERENCES customer (customer_id)
);
""";
        const string after = """
CREATE TABLE customer
(
    customer_id integer PRIMARY KEY,
    full_name   varchar(200) NULL,
    email       varchar(320) NOT NULL
);

CREATE TABLE orders
(
    order_id    integer PRIMARY KEY,
    customer_id integer NOT NULL REFERENCES customer (customer_id)
);
""";

        await RunDeployScenarioAsync(
            before, after,
            seedSql: """
INSERT INTO customer (customer_id, email) VALUES (1, 'a@example.com');
INSERT INTO orders (order_id, customer_id) VALUES (10, 1);
""",
            assertAfterAsync: async conn =>
            {
                // Both tables' rows survive the rebuild.
                Assert.Equal(1L, await ScalarAsync(conn, "SELECT count(*) FROM customer;"));
                Assert.Equal(1L, await ScalarAsync(conn, "SELECT count(*) FROM orders;"));

                // The FK is back and enforcing: an order referencing a missing customer fails.
                await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(
                    conn, "INSERT INTO orders (order_id, customer_id) VALUES (11, 999);",
                    TestContext.Current.CancellationToken));

                // The FK still points at customer (one inbound FK on orders).
                var fkCount = await ScalarAsync(conn, """
SELECT count(*) FROM pg_constraint c
JOIN pg_class t ON t.oid = c.conrelid
JOIN pg_class rt ON rt.oid = c.confrelid
WHERE c.contype = 'f' AND t.relname = 'orders' AND rt.relname = 'customer';
""");
                Assert.Equal(1L, fkCount);
            });
    }

    [Fact]
    public async Task InsertColumnBetweenExisting_WhenRebuildDisallowed_FailsAndLeavesTableUnchanged()
    {
        const string before = """
CREATE TABLE customer
(
    customer_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email       varchar(320) NOT NULL
);
""";
        const string after = """
CREATE TABLE customer
(
    customer_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    full_name   varchar(200) NULL,
    email       varchar(320) NOT NULL
);
""";

        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-alter-norebuild");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_alter_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var beforeDacpac = await BuildDacpacAsync(tempDir.FullName, "before", before, ct);
                await DacpacDeployer.DeployFromFileAsync(
                    beforeDacpac, ConnectionString, targetDbName, cancellationToken: ct);

                await using (var seedConn = await OpenAsync(targetDbName, ct))
                {
                    await ExecuteAsync(
                        seedConn, "INSERT INTO customer (email) VALUES ('a@example.com');", ct);
                }

                var afterDacpac = await BuildDacpacAsync(tempDir.FullName, "after", after, ct);

                // Deploying with rebuilds disallowed must fail rather than rebuild.
                await Assert.ThrowsAsync<TableRebuildNotAllowedException>(() =>
                    DacpacDeployer.DeployFromFileAsync(
                        afterDacpac, ConnectionString, targetDbName,
                        options: new DeployOptions { AllowTableRebuild = false },
                        cancellationToken: ct));

                // The original table and its data must be untouched by the failed deploy.
                await using var conn = await OpenAsync(targetDbName, ct);
                var count = await ScalarAsync(conn, "SELECT count(*) FROM customer;");
                Assert.Equal(1L, count);

                // full_name must not exist — the change was not applied.
                var hasColumn = await ScalarAsync(conn, """
SELECT count(*) FROM information_schema.columns
WHERE table_name = 'customer' AND column_name = 'full_name';
""");
                Assert.Equal(0L, hasColumn);
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
        var tempDir = Directory.CreateTempSubdirectory("squill-alter-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_alter_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                // Deploy the initial schema.
                var beforeDacpac = await BuildDacpacAsync(tempDir.FullName, "before", before, ct);
                await DacpacDeployer.DeployFromFileAsync(
                    beforeDacpac, ConnectionString, targetDbName, cancellationToken: ct);

                // Seed data so we can prove it survives the schema change.
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
                    "A schema change should generate a non-empty script.");

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

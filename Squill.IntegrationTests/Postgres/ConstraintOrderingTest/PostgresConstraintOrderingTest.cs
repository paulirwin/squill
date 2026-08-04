using Npgsql;
using Squill.Core;
using Squill.Dacpac;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.ConstraintOrderingTest;

// Adding a column and a constraint or index defined ON that column in the same change (issue
// #200). The delta for the constraint and the delta for the column are separate, and the
// constraint used to be ordered into the creates phase ahead of the ALTER that adds its column
// — so the deploy ran `ADD CONSTRAINT` against a column that did not exist yet.
//
// This is the half that only a real server can prove. The unit tests in
// Squill.Provider.Postgres.Tests pin the ORDER of the emitted statements, but ordering is only
// a proxy for the thing that actually matters: whether PostgreSQL accepts the script. Before the
// fix the first statement failed with 42703 (undefined_column) and the deploy aborted with the
// column unadded and the constraint uncreated.
public class PostgresConstraintOrderingTest : PostgresIntegrationTestBase
{
    /// <summary>
    /// A UNIQUE constraint on a column added in the same deploy. The constraint has to follow
    /// its column, and the resulting constraint must actually be enforced.
    /// </summary>
    [Fact]
    public async Task AddColumnAndUniqueConstraintOnIt_Deploys_AndEnforcesTheConstraint()
    {
        await RunDeployScenarioAsync(
            "CREATE TABLE orders (id integer PRIMARY KEY);",
            """
CREATE TABLE orders
(
    id    integer PRIMARY KEY,
    email text,
    CONSTRAINT uq_orders_email UNIQUE (email)
);
""",
            seedSql: "INSERT INTO orders (id) VALUES (1);",
            assertAfterAsync: async conn =>
            {
                // The pre-existing row survives the change.
                Assert.Equal(1L, await ScalarAsync(conn, "SELECT count(*) FROM orders;"));

                // The constraint is live: the duplicate is refused.
                await ExecuteAsync(
                    conn, "INSERT INTO orders (id, email) VALUES (2, 'a@example.com');",
                    TestContext.Current.CancellationToken);

                await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(
                    conn, "INSERT INTO orders (id, email) VALUES (3, 'a@example.com');",
                    TestContext.Current.CancellationToken));
            });
    }

    /// <summary>
    /// The same for a CHECK constraint, whose predicate names the new column.
    /// </summary>
    [Fact]
    public async Task AddColumnAndCheckConstraintOnIt_Deploys_AndEnforcesTheConstraint()
    {
        await RunDeployScenarioAsync(
            "CREATE TABLE orders (id integer PRIMARY KEY);",
            """
CREATE TABLE orders
(
    id       integer PRIMARY KEY,
    quantity integer,
    CONSTRAINT ck_orders_quantity CHECK (quantity > 0)
);
""",
            seedSql: "INSERT INTO orders (id) VALUES (1);",
            assertAfterAsync: async conn =>
            {
                Assert.Equal(1L, await ScalarAsync(conn, "SELECT count(*) FROM orders;"));

                await ExecuteAsync(
                    conn, "INSERT INTO orders (id, quantity) VALUES (2, 5);",
                    TestContext.Current.CancellationToken);

                await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteAsync(
                    conn, "INSERT INTO orders (id, quantity) VALUES (3, 0);",
                    TestContext.Current.CancellationToken));
            });
    }

    /// <summary>
    /// And for an index, which names the column in CREATE INDEX rather than in an ALTER.
    /// </summary>
    [Fact]
    public async Task AddColumnAndIndexOnIt_Deploys()
    {
        await RunDeployScenarioAsync(
            "CREATE TABLE orders (id integer PRIMARY KEY);",
            """
CREATE TABLE orders
(
    id    integer PRIMARY KEY,
    email text
);
CREATE INDEX ix_orders_email ON orders (email);
""",
            seedSql: "INSERT INTO orders (id) VALUES (1);",
            assertAfterAsync: async conn =>
            {
                Assert.Equal(1L, await ScalarAsync(conn, "SELECT count(*) FROM orders;"));

                Assert.Equal(1L, await ScalarAsync(
                    conn,
                    """
SELECT count(*) FROM pg_indexes
WHERE schemaname = 'public' AND tablename = 'orders' AND indexname = 'ix_orders_email';
"""));
            });
    }

    /// <summary>
    /// Two same-named tables in different schemas, where dropping one must not suppress the
    /// reconciliation of the other's constraints (issue #200). Postgres names both primary keys
    /// <c>orders_pkey</c>, so before the fix the two were indistinguishable in the model and the
    /// compare threw before producing a single delta.
    /// </summary>
    [Fact]
    public async Task DroppingATable_StillDropsConstraints_OnASameNamedTableInAnotherSchema()
    {
        await RunDeployScenarioAsync(
            """
CREATE SCHEMA staging;
CREATE TABLE orders
(
    id    integer PRIMARY KEY,
    email text,
    CONSTRAINT uq_orders_email UNIQUE (email)
);
CREATE TABLE staging.orders (id integer PRIMARY KEY);
""",
            """
CREATE SCHEMA staging;
CREATE TABLE orders
(
    id    integer PRIMARY KEY,
    email text
);
""",
            seedSql: "INSERT INTO orders (id, email) VALUES (1, 'a@example.com');",
            afterOptions: new DeployOptions
            {
                DropObjectsNotInSource = true,
                BlockOnPossibleDataLoss = false,
            },
            assertAfterAsync: async conn =>
            {
                // public.orders survives with its data; staging.orders is gone.
                Assert.Equal(1L, await ScalarAsync(conn, "SELECT count(*) FROM orders;"));
                Assert.Equal(0L, await ScalarAsync(
                    conn,
                    """
SELECT count(*) FROM information_schema.tables
WHERE table_schema = 'staging' AND table_name = 'orders';
"""));

                // The unique constraint on the SURVIVING table was really dropped, so the
                // duplicate that it used to refuse is now accepted.
                Assert.Equal(0L, await ScalarAsync(
                    conn,
                    """
SELECT count(*) FROM pg_constraint
WHERE conname = 'uq_orders_email';
"""));

                await ExecuteAsync(
                    conn, "INSERT INTO orders (id, email) VALUES (2, 'a@example.com');",
                    TestContext.Current.CancellationToken);
                Assert.Equal(2L, await ScalarAsync(conn, "SELECT count(*) FROM orders;"));
            });
    }

    // Deploys BEFORE, seeds it, deploys AFTER, then asserts the deployed database matches the
    // AFTER model and hands the caller a connection for its own assertions. The deploy of AFTER
    // is the step under test: before the fix it aborted with SQLSTATE 42703.
    private async Task RunDeployScenarioAsync(
        string before,
        string after,
        string seedSql,
        Func<NpgsqlConnection, Task> assertAfterAsync,
        DeployOptions? afterOptions = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-constraint-ordering-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_constraintorder_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var beforeDacpac = await BuildDacpacAsync(tempDir.FullName, "before", before, ct);
                await DacpacDeployer.DeployFromFileAsync(
                    beforeDacpac, ConnectionString, targetDbName, cancellationToken: ct);

                await using (var seedConn = await OpenAsync(targetDbName, ct))
                {
                    await ExecuteAsync(seedConn, seedSql, ct);
                }

                var afterDacpac = await BuildDacpacAsync(tempDir.FullName, "after", after, ct);
                var result = await DacpacDeployer.DeployFromFileAsync(
                    afterDacpac, ConnectionString, targetDbName, options: afterOptions,
                    cancellationToken: ct);

                Assert.True(result.WasExecuted);

                // The database's model must now match the changed DACPAC's model.
                Model afterModel;
                await using (var stream = File.OpenRead(afterDacpac))
                {
                    (_, afterModel) = await DacpacSerializer.Deserialize(
                        stream, provider.DependencyAnalyzer, ct);
                }

                var deployedModel = await provider
                    .CreateDatabaseModelBuilder(createdDb)
                    .ExtractModelAsync(ct);

                Assert.Equal(
                    ModelAssertions.ElementHashMultiset(afterModel),
                    ModelAssertions.ElementHashMultiset(deployedModel));

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

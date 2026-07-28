using Npgsql;
using Squill.Core;
using Squill.Dacpac;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.ColumnConstraintAlterTest;

// Nullability changes, NOT NULL column adds, and constraint adds/drops applied to tables that
// already hold rows (issue #137, EF Core coverage parity). Everything here is the shape of change
// a user actually makes to a live database, so the two things being proven are (a) the changes
// Postgres can apply do apply and preserve the rows, and (b) the changes Postgres *rejects* fail
// without leaving a half-applied schema behind — a mid-script failure that partially mutates the
// target is the worst outcome a deployment tool can have.
//
// Scenarios modelled on the EF Core Npgsql provider's migration tests
// (https://github.com/npgsql/efcore.pg, PostgreSQL License, Copyright (c) 2002-2021, Npgsql);
// the SQL below is original, so no verbatim-copy attribution header is required.
public class PostgresColumnConstraintAlterTest : PostgresIntegrationTestBase
{
    private const string PeopleNullable = """
CREATE TABLE people
(
    id          integer PRIMARY KEY,
    some_column text NULL
);
""";

    private const string PeopleNotNull = """
CREATE TABLE people
(
    id          integer PRIMARY KEY,
    some_column text NOT NULL
);
""";

    /// <summary>
    /// Making a column required while the table still holds NULLs is rejected by Postgres
    /// (SQLSTATE 23502) because Squill emits a bare <c>SET NOT NULL</c> with no backfill. The
    /// valuable half of this test is the second part: the failed deploy must leave the column
    /// nullable and the row intact, not a half-applied schema.
    /// </summary>
    [Fact]
    public async Task MakeColumnRequired_WhenTableHasNulls_FailsAndLeavesTableUnchanged()
    {
        await RunFailingScenarioAsync(
            PeopleNullable, PeopleNotNull,
            seedSql: "INSERT INTO people (id, some_column) VALUES (1, NULL);",
            assertUnchangedAsync: async conn =>
            {
                // The row survives, and the column is still nullable.
                Assert.Equal(1L, await ScalarAsync(conn, "SELECT count(*) FROM people;"));
                Assert.Equal(1L, await ScalarAsync(
                    conn, "SELECT count(*) FROM people WHERE some_column IS NULL;"));
                Assert.False(await IsNotNullAsync(conn, "people", "some_column"));
            },
            expectedSqlState: PostgresErrorCodes.NotNullViolation);
    }

    /// <summary>
    /// The control case for the above: the same SET NOT NULL against rows that are all non-null
    /// is exactly what Postgres allows, so it must deploy and keep the data.
    /// </summary>
    [Fact]
    public async Task MakeColumnRequired_WhenTableHasNoNulls_AltersInPlace_AndPreservesData()
    {
        await RunDeployScenarioAsync(
            PeopleNullable, PeopleNotNull,
            seedSql: """
INSERT INTO people (id, some_column) VALUES (1, 'a'), (2, 'b');
""",
            assertAfterAsync: async conn =>
            {
                Assert.Equal(2L, await ScalarAsync(conn, "SELECT count(*) FROM people;"));
                Assert.True(await IsNotNullAsync(conn, "people", "some_column"));

                // The constraint is live: a NULL insert is now refused.
                await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                    conn, "INSERT INTO people (id, some_column) VALUES (3, NULL);",
                    TestContext.Current.CancellationToken));
            });
    }

    /// <summary>
    /// The reverse direction — NOT NULL to nullable — is always safe and must go through
    /// <c>DROP NOT NULL</c> rather than a rebuild.
    /// </summary>
    [Fact]
    public async Task MakeColumnNullable_AltersInPlace_AndPreservesData()
    {
        await RunDeployScenarioAsync(
            PeopleNotNull, PeopleNullable,
            seedSql: "INSERT INTO people (id, some_column) VALUES (1, 'a');",
            assertAfterAsync: async conn =>
            {
                Assert.Equal(1L, await ScalarAsync(conn, "SELECT count(*) FROM people;"));
                Assert.False(await IsNotNullAsync(conn, "people", "some_column"));

                // Now that it is nullable, a NULL is accepted.
                await ExecuteAsync(conn, "INSERT INTO people (id) VALUES (2);",
                    TestContext.Current.CancellationToken);
                Assert.Equal(1L, await ScalarAsync(
                    conn, "SELECT count(*) FROM people WHERE some_column IS NULL;"));
            },
            assertScript: script => Assert.Contains("DROP NOT NULL", script));
    }

    /// <summary>
    /// Adding a NOT NULL column *with* a default to a populated table is fine: Postgres backfills
    /// the existing rows from the default, so the pre-existing rows come out with '' rather than
    /// NULL.
    /// </summary>
    [Fact]
    public async Task AddNotNullColumnWithDefault_ToPopulatedTable_Backfills()
    {
        const string before = """
CREATE TABLE customers
(
    id integer PRIMARY KEY
);
""";
        const string after = """
CREATE TABLE customers
(
    id   integer PRIMARY KEY,
    name text NOT NULL DEFAULT ''
);
""";

        await RunDeployScenarioAsync(
            before, after,
            seedSql: "INSERT INTO customers (id) VALUES (1), (2);",
            assertAfterAsync: async conn =>
            {
                Assert.Equal(2L, await ScalarAsync(conn, "SELECT count(*) FROM customers;"));

                // Backfilled from the default, not left NULL.
                Assert.Equal(2L, await ScalarAsync(
                    conn, "SELECT count(*) FROM customers WHERE name = '';"));
                Assert.True(await IsNotNullAsync(conn, "customers", "name"));
            });
    }

    /// <summary>
    /// The same add without a default cannot work against a populated table: Postgres has nothing
    /// to put in the existing rows and rejects the ADD COLUMN (SQLSTATE 23502). Squill emits the
    /// column definition verbatim with no gate, so the failure surfaces from the engine at deploy
    /// time. A build- or plan-time diagnostic (the way BlockOnPossibleDataLoss gates a destructive
    /// change before any SQL runs) would be the better behaviour here; what this test locks in for
    /// now is that the failure is clean — the table is untouched afterwards.
    /// </summary>
    [Fact]
    public async Task AddNotNullColumnWithoutDefault_ToPopulatedTable_FailsAndLeavesTableUnchanged()
    {
        const string before = """
CREATE TABLE customers
(
    id integer PRIMARY KEY
);
""";
        const string after = """
CREATE TABLE customers
(
    id   integer PRIMARY KEY,
    name text NOT NULL
);
""";

        await RunFailingScenarioAsync(
            before, after,
            seedSql: "INSERT INTO customers (id) VALUES (1), (2);",
            assertUnchangedAsync: async conn =>
            {
                Assert.Equal(2L, await ScalarAsync(conn, "SELECT count(*) FROM customers;"));

                // The column must not have been added by the failed deploy.
                Assert.Equal(0L, await ScalarAsync(conn, """
SELECT count(*) FROM information_schema.columns
WHERE table_name = 'customers' AND column_name = 'name';
"""));
            },
            expectedSqlState: PostgresErrorCodes.NotNullViolation);
    }

    // Dropping a generated column together with the columns its expression reads is not covered
    // here: Squill emits the per-column DROPs in diff order with no dependency sort, so it drops
    // `x` while the generated `sum` still depends on it and Postgres refuses (SQLSTATE 2BP01).
    // The same drops in dependency order are accepted by Postgres, so this is a Squill defect
    // rather than an engine limitation — reported separately, not asserted here.

    /// <summary>
    /// Dropping the column that carries the primary key: the PK constraint has to go before (or
    /// with) the column. A PK is attached to its table's delta by
    /// <c>PostgresDatabaseDependencyAnalyzer</c>, so this takes the rebuild path; what matters is
    /// the end state — no PK left on the table, no <c>id</c> column, and the row's other data
    /// preserved.
    /// </summary>
    [Fact]
    public async Task DropPrimaryKeyColumn_RemovesConstraintAndColumn()
    {
        const string before = """
CREATE TABLE people
(
    id   integer PRIMARY KEY,
    name text NULL
);
""";
        const string after = """
CREATE TABLE people
(
    name text NULL
);
""";

        await RunDeployScenarioAsync(
            before, after,
            seedSql: "INSERT INTO people (id, name) VALUES (1, 'a');",
            assertAfterAsync: async conn =>
            {
                Assert.Equal(1L, await ScalarAsync(conn, "SELECT count(*) FROM people;"));
                Assert.Equal("a", await ScalarAsync(conn, "SELECT name FROM people;"));

                // No primary key remains.
                Assert.Equal(0L, await ScalarAsync(conn, """
SELECT count(*) FROM pg_constraint
WHERE conrelid = 'people'::regclass AND contype = 'p';
"""));

                Assert.Equal(0L, await ScalarAsync(conn, """
SELECT count(*) FROM information_schema.columns
WHERE table_name = 'people' AND column_name = 'id';
"""));
            },
            afterOptions: new DeployOptions { BlockOnPossibleDataLoss = false });
    }

    /// <summary>
    /// Removing a foreign key from the source, with <c>DropObjectsNotInSource</c> enabled, must
    /// drop it from the database.
    /// </summary>
    /// <remarks>
    /// <c>SchemaCompare.AddDropDeltas</c> skips dependent constraints that are not droppable
    /// standalone dependents, and a foreign key is one of those — a PK/FK is only reconciled
    /// through its table, and dropping the FK does not change the table's hash, so no delta is
    /// produced. This is the drop direction of the same defect the add direction hits; it at
    /// least fails safe.
    /// </remarks>
    [Fact(Skip = "Blocked by issue #157: SchemaCompare skips dependent elements on an otherwise "
                 + "unchanged table, so dropping a FOREIGN KEY produces no delta and it stays "
                 + "in the database enforcing a relationship the source no longer declares.")]
    public async Task DropForeignKey_AsTheOnlyChange_DropsTheConstraint()
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-column-constraint-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_colconstraint_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var beforeDacpac = await BuildDacpacAsync(
                    tempDir.FullName, "before", OrdersWithForeignKey, ct);
                await DacpacDeployer.DeployFromFileAsync(
                    beforeDacpac, ConnectionString, targetDbName, cancellationToken: ct);

                await using (var seedConn = await OpenAsync(targetDbName, ct))
                {
                    await ExecuteAsync(seedConn, SatisfyingRows, ct);
                }

                var afterDacpac = await BuildDacpacAsync(
                    tempDir.FullName, "after", OrdersWithoutForeignKey, ct);
                var result = await DacpacDeployer.DeployFromFileAsync(
                    afterDacpac, ConnectionString, targetDbName,
                    options: new DeployOptions
                    {
                        DropObjectsNotInSource = true,
                        BlockOnPossibleDataLoss = false,
                    },
                    cancellationToken: ct);

                await using var conn = await OpenAsync(targetDbName, ct);

                // The constraint the source no longer declares must be gone.
                Assert.Equal(0L, await ScalarAsync(conn, """
SELECT count(*) FROM pg_constraint
WHERE conrelid = 'orders'::regclass AND contype = 'f';
"""));

                Assert.False(string.IsNullOrWhiteSpace(result.Script),
                    "Dropping a foreign key should generate a non-empty script.");

                // With the FK gone, a row that would have violated it is accepted.
                await ExecuteAsync(
                    conn, "INSERT INTO orders (id, customer_id) VALUES (99, 999);", ct);
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

    private const string OrdersWithoutForeignKey = """
CREATE TABLE customers
(
    id integer PRIMARY KEY
);

CREATE TABLE orders
(
    id          integer PRIMARY KEY,
    customer_id integer NOT NULL
);
""";

    private const string OrdersWithForeignKey = """
CREATE TABLE customers
(
    id integer PRIMARY KEY
);

CREATE TABLE orders
(
    id          integer PRIMARY KEY,
    customer_id integer NOT NULL,
    CONSTRAINT fk_orders_customer FOREIGN KEY (customer_id)
        REFERENCES customers (id) ON DELETE CASCADE
);
""";

    private const string SatisfyingRows = """
INSERT INTO customers (id) VALUES (1);
INSERT INTO orders (id, customer_id) VALUES (10, 1);
""";

    // Deploys `before`, seeds data, deploys `after` to the same database, verifies the database's
    // model matches the `after` DACPAC, then runs the caller's assertions.
    private async Task RunDeployScenarioAsync(
        string before,
        string after,
        string seedSql,
        Func<NpgsqlConnection, Task> assertAfterAsync,
        DeployOptions? afterOptions = null,
        Action<string>? assertScript = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-column-constraint-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_colconstraint_{Guid.NewGuid():n}";
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
                Assert.False(string.IsNullOrWhiteSpace(result.Script),
                    "A schema change should generate a non-empty script.");

                assertScript?.Invoke(result.Script);

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

    // The same shape, but the second deploy is expected to be rejected by the engine. Asserts the
    // SQLSTATE, then reconnects and hands the caller a connection so it can prove the database is
    // still in its BEFORE state — nothing half-applied.
    private async Task RunFailingScenarioAsync(
        string before,
        string after,
        string seedSql,
        Func<NpgsqlConnection, Task> assertUnchangedAsync,
        string expectedSqlState,
        DeployOptions? afterOptions = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-column-constraint-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_colconstraint_{Guid.NewGuid():n}";
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

                var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                    DacpacDeployer.DeployFromFileAsync(
                        afterDacpac, ConnectionString, targetDbName, options: afterOptions,
                        cancellationToken: ct));

                Assert.Equal(expectedSqlState, ex.SqlState);

                // A failed deploy must not leave the target partly changed.
                await using var conn = await OpenAsync(targetDbName, ct);
                await assertUnchangedAsync(conn);
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

    // pg_attribute.attnotnull is the authoritative NOT NULL flag for a column.
    private static async Task<bool> IsNotNullAsync(
        NpgsqlConnection conn, string table, string column)
    {
        await using var cmd = new NpgsqlCommand("""
SELECT attnotnull FROM pg_attribute
WHERE attrelid = @table::regclass AND attname = @column AND attnum > 0 AND NOT attisdropped;
""", conn);
        cmd.Parameters.AddWithValue("table", table);
        cmd.Parameters.AddWithValue("column", column);

        var result = await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Assert.IsType<bool>(result);
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

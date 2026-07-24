using Npgsql;
using Squill.Core;
using Squill.Dacpac;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.CheckAndGeneratedTest;

// End-to-end coverage against real Postgres for issue #120: CHECK constraints (column and
// table level) and generated (computed) columns. A CHECK was previously dropped during the
// build with only a warning, and a generated column threw outright. Each scenario deploys
// through the same DacpacDeployer code path the CLI uses and then verifies the deployed
// database's model hash-matches the DACPAC's — which is what proves the parser-built model
// and the extracted model agree, so the schema does not re-diff on the next deploy.
public class PostgresCheckAndGeneratedTest : PostgresIntegrationTestBase
{
    [Fact]
    public async Task CheckConstraints_RoundTripAndAreEnforced()
    {
        const string schema = """
CREATE TABLE product
(
    product_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    price      numeric NOT NULL CHECK (price > 0),
    stock      integer NOT NULL,
    reorder_at integer NOT NULL,
    CONSTRAINT ck_stock_nonnegative CHECK (stock >= 0),
    CONSTRAINT ck_reorder_below_stock CHECK (reorder_at <= stock)
);
""";

        await RunDeployScenarioAsync(schema, async conn =>
        {
            // All three must exist in pg_constraint as real CHECK constraints (contype 'c').
            var names = await CheckConstraintNamesAsync(conn, "product");

            Assert.Equal(
                ["ck_reorder_below_stock", "ck_stock_nonnegative", "product_price_check"],
                names);

            // And they must actually be enforced.
            await ExecuteAsync(conn,
                "INSERT INTO product (price, stock, reorder_at) VALUES (9.99, 10, 5);",
                TestContext.Current.CancellationToken);

            // price > 0
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO product (price, stock, reorder_at) VALUES (0, 10, 5);",
                TestContext.Current.CancellationToken));

            // stock >= 0
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO product (price, stock, reorder_at) VALUES (1, -1, 0);",
                TestContext.Current.CancellationToken));

            // reorder_at <= stock — a predicate spanning two columns.
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO product (price, stock, reorder_at) VALUES (1, 5, 10);",
                TestContext.Current.CancellationToken));
        });
    }

    /// <summary>
    /// A CHECK on a table in a non-public schema must deploy and extract with its table
    /// correctly qualified, rather than resolving against the session search_path.
    /// </summary>
    [Fact]
    public async Task CheckConstraint_InNonPublicSchema_RoundTrips()
    {
        const string schema = """
CREATE SCHEMA staging;

CREATE TABLE staging.account
(
    account_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    balance    numeric NOT NULL,
    CONSTRAINT ck_balance_nonnegative CHECK (balance >= 0)
);
""";

        await RunDeployScenarioAsync(schema, async conn =>
        {
            var conname = (string?)await ScalarAsync(conn, """
SELECT conname FROM pg_constraint
WHERE conrelid = 'staging.account'::regclass AND contype = 'c';
""");
            Assert.Equal("ck_balance_nonnegative", conname);

            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO staging.account (balance) VALUES (-1);",
                TestContext.Current.CancellationToken));
        });
    }

    /// <summary>
    /// Adding a CHECK to a table that already exists has no CREATE TABLE to carry the
    /// clause, so it must be deployed as a standalone ALTER TABLE ... ADD CONSTRAINT.
    /// </summary>
    [Fact]
    public async Task AddCheckToExistingTable_AddsConstraint_AndPreservesData()
    {
        const string before = """
CREATE TABLE account
(
    account_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    balance    numeric NOT NULL
);
""";
        const string after = """
CREATE TABLE account
(
    account_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    balance    numeric NOT NULL,
    CONSTRAINT ck_balance_nonnegative CHECK (balance >= 0)
);
""";

        await RunIncrementalDeployScenarioAsync(
            before, after,
            seedSql: "INSERT INTO account (balance) VALUES (100);",
            assertAfterAsync: async conn =>
            {
                var conname = (string?)await ScalarAsync(conn, """
SELECT conname FROM pg_constraint
WHERE conrelid = 'account'::regclass AND contype = 'c';
""");
                Assert.Equal("ck_balance_nonnegative", conname);

                // The seeded row must survive, and the constraint must now be enforced.
                var count = await ScalarAsync(conn, "SELECT count(*) FROM account;");
                Assert.Equal(1L, count);

                await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                    "INSERT INTO account (balance) VALUES (-1);",
                    TestContext.Current.CancellationToken));
            });
    }

    /// <summary>
    /// Deploying the same schema twice must be a no-op. This is the regression that matters
    /// most for a construct whose expression the engine rewrites: if the declared predicate
    /// were compared against the rewritten one, the constraint would re-diff forever.
    /// </summary>
    [Fact]
    public async Task RedeployingCheckAndGeneratedColumn_ProducesNoChanges()
    {
        const string schema = """
CREATE TABLE line_item
(
    line_item_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    price        numeric NOT NULL,
    quantity     integer NOT NULL,
    total        numeric GENERATED ALWAYS AS (price * quantity) STORED,
    CONSTRAINT ck_quantity_positive CHECK (quantity > 0)
);
""";

        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-check-generated-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_check_generated_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var dacpac = await BuildDacpacAsync(tempDir.FullName, "schema", schema, ct);

                await DacpacDeployer.DeployFromFileAsync(
                    dacpac, ConnectionString, targetDbName, cancellationToken: ct);

                // The second deploy of an unchanged schema must generate nothing.
                var second = await DacpacDeployer.DeployFromFileAsync(
                    dacpac, ConnectionString, targetDbName, cancellationToken: ct);

                Assert.True(string.IsNullOrWhiteSpace(second.Script),
                    "Redeploying an unchanged schema must not generate a script; "
                    + $"got: {second.Script}");
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
    public async Task GeneratedColumn_RoundTripsAndComputesItsValue()
    {
        const string schema = """
CREATE TABLE line_item
(
    line_item_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    price        numeric NOT NULL,
    quantity     integer NOT NULL,
    total        numeric GENERATED ALWAYS AS (price * quantity) STORED
);
""";

        await RunDeployScenarioAsync(schema, async conn =>
        {
            // The column must be a real generated column in the catalog.
            var generated = (string?)await ScalarAsync(conn, """
SELECT a.attgenerated::text FROM pg_attribute a
WHERE a.attrelid = 'line_item'::regclass AND a.attname = 'total';
""");
            Assert.Equal("s", generated);

            await ExecuteAsync(conn,
                "INSERT INTO line_item (price, quantity) VALUES (2.50, 4);",
                TestContext.Current.CancellationToken);

            // And the engine must compute its value.
            var total = await ScalarAsync(conn, "SELECT total FROM line_item;");
            Assert.Equal(10.00m, Assert.IsType<decimal>(total));

            // A generated column cannot be written to directly.
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO line_item (price, quantity, total) VALUES (1, 1, 99);",
                TestContext.Current.CancellationToken));
        });
    }

    /// <summary>
    /// String concatenation reaches the parser through the general operator path rather
    /// than one of the fixed math operators, and is a common generation expression.
    /// </summary>
    [Fact]
    public async Task GeneratedColumn_WithConcatenation_RoundTripsAndComputesItsValue()
    {
        const string schema = """
CREATE TABLE person
(
    person_id  integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    first_name text NOT NULL,
    last_name  text NOT NULL,
    full_name  text GENERATED ALWAYS AS (first_name || ' ' || last_name) STORED
);
""";

        await RunDeployScenarioAsync(schema, async conn =>
        {
            await ExecuteAsync(conn,
                "INSERT INTO person (first_name, last_name) VALUES ('Ada', 'Lovelace');",
                TestContext.Current.CancellationToken);

            var fullName = (string?)await ScalarAsync(conn, "SELECT full_name FROM person;");
            Assert.Equal("Ada Lovelace", fullName);
        });
    }

    [Fact]
    public async Task NotNullGeneratedColumn_RoundTrips()
    {
        const string schema = """
CREATE TABLE line_item
(
    line_item_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    price        numeric NOT NULL,
    doubled      numeric NOT NULL GENERATED ALWAYS AS (price * 2) STORED
);
""";

        await RunDeployScenarioAsync(schema, async conn =>
        {
            var nullable = (bool?)await ScalarAsync(conn, """
SELECT a.attnotnull FROM pg_attribute a
WHERE a.attrelid = 'line_item'::regclass AND a.attname = 'doubled';
""");
            Assert.True(nullable);

            await ExecuteAsync(conn,
                "INSERT INTO line_item (price) VALUES (3);",
                TestContext.Current.CancellationToken);

            var doubled = await ScalarAsync(conn, "SELECT doubled FROM line_item;");
            Assert.Equal(6m, Assert.IsType<decimal>(doubled));
        });
    }

    private static async Task<List<string>> CheckConstraintNamesAsync(
        NpgsqlConnection conn, string table)
    {
        // conislocal excludes constraints inherited from a parent table; the NOT NULL
        // exclusion mirrors the model builder, since PostgreSQL 18+ records those here too.
        await using var cmd = new NpgsqlCommand($"""
SELECT conname FROM pg_constraint
WHERE conrelid = '{table}'::regclass
  AND contype = 'c'
  AND conislocal
  AND pg_get_constraintdef(oid) NOT LIKE '%IS NOT NULL%'
ORDER BY conname;
""", conn);

        var names = new List<string>();

        await using var reader = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    // Deploys a schema to a fresh database, verifies the deployed database's model
    // hash-matches the DACPAC's, then runs the caller's assertions against it.
    private async Task RunDeployScenarioAsync(
        string schema, Func<NpgsqlConnection, Task> assertAsync)
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-check-generated-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_check_generated_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var dacpac = await BuildDacpacAsync(tempDir.FullName, "schema", schema, ct);

                await DacpacDeployer.DeployFromFileAsync(
                    dacpac, ConnectionString, targetDbName, cancellationToken: ct);

                await AssertModelsMatchAsync(provider, createdDb, dacpac, ct);

                await using var conn = await OpenAsync(targetDbName, ct);
                await assertAsync(conn);
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

    // Deploys `before`, seeds data, then deploys `after` to the same database — the
    // incremental path, where the table already exists.
    private async Task RunIncrementalDeployScenarioAsync(
        string before,
        string after,
        string seedSql,
        Func<NpgsqlConnection, Task> assertAfterAsync)
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-check-generated-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_check_generated_{Guid.NewGuid():n}";
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
                    afterDacpac, ConnectionString, targetDbName, cancellationToken: ct);

                Assert.True(result.WasExecuted);
                Assert.False(string.IsNullOrWhiteSpace(result.Script),
                    "Adding a constraint to an existing table should generate a non-empty script.");

                await AssertModelsMatchAsync(provider, createdDb, afterDacpac, ct);

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

    // The deployed database's model must hash-match the DACPAC's, which is what proves the
    // parser-built and database-extracted models agree on these constructs.
    private static async Task AssertModelsMatchAsync(
        IDatabaseProvider provider, IDatabase createdDb, string dacpacPath, CancellationToken ct)
    {
        Model dacpacModel;
        await using (var stream = File.OpenRead(dacpacPath))
        {
            // The provider's dependency analyzer supplies the identity rules, which is how
            // a property that opts out of identity (a CHECK predicate, a generation
            // expression) keeps that flag when read back from a DACPAC — the flag itself is
            // not stored in the XML (issue #122). This is the same overload the deployer
            // uses; without it every property would participate and the hashes would differ.
            (_, dacpacModel) = await DacpacSerializer.Deserialize(
                stream, provider.DependencyAnalyzer, ct);
        }

        var deployedModel = await provider
            .CreateDatabaseModelBuilder(createdDb)
            .ExtractModelAsync(ct);

        Assert.Equal(
            ModelAssertions.ElementHashMultiset(dacpacModel),
            ModelAssertions.ElementHashMultiset(deployedModel));
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

using Npgsql;
using Squill.Core;
using Squill.Dacpac;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.ExpressionRedefinitionTest;

// Characterization coverage for issue #137: what happens end-to-end when a CHECK predicate
// or a generated column's expression is REDEFINED under the same name.
//
// Background — DatabaseDependencyAnalyzerBase.ParticipatesInIdentity deliberately excludes
// (SqlCheckConstraint, CheckExpression) and (SqlSimpleColumn, GeneratedExpression) from the
// Merkle hash, because every engine rewrites the expression text it is given, so a declared
// predicate could never hash-match an extracted one (issue #120). The untested consequence
// these tests pin down: because the expression is not hashed, changing ONLY the expression
// changes no hash, SchemaCompare emits no delta, and the deploy reports success while the
// old predicate/expression stays live in the database.
//
// The tests below assert the CORRECT behaviour. Those blocked by a known defect carry a
// [Fact(Skip = ...)] naming the issue, so they go green on their own once it is fixed. The
// unskipped tests guard the paths that already work.
public class PostgresExpressionRedefinitionTest : PostgresIntegrationTestBase
{
    /// <summary>
    /// Redefining a CHECK predicate under the same constraint name must drop and re-add the
    /// constraint so the declared predicate wins.
    /// </summary>
    [Fact]
    public async Task ChangedCheckPredicate_UnderSameConstraintName_IsApplied()
    {
        const string before = """
CREATE TABLE people
(
    id             integer PRIMARY KEY,
    driver_license integer NOT NULL,
    CONSTRAINT ck_people CHECK (driver_license > 0)
);
""";
        const string after = """
CREATE TABLE people
(
    id             integer PRIMARY KEY,
    driver_license integer NOT NULL,
    CONSTRAINT ck_people CHECK (driver_license > 10)
);
""";

        await RunScenarioAsync(
            before, after,
            seedSql: "INSERT INTO people (id, driver_license) VALUES (1, 100);",
            assertAfterAsync: async (conn, result) =>
            {
                // The predicate changed, so the deploy must do something about it.
                Assert.False(string.IsNullOrWhiteSpace(result.Script),
                    "A changed CHECK predicate should generate a non-empty script.");

                // The database must carry the DECLARED predicate.
                var def = (string?)await ScalarAsync(conn, """
SELECT pg_get_constraintdef(oid) FROM pg_constraint
WHERE conrelid = 'people'::regclass AND conname = 'ck_people';
""");
                Assert.Equal("CHECK ((driver_license > 10))", def);

                // The user-visible point: a row the declared schema forbids must be rejected.
                await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                    conn,
                    "INSERT INTO people (id, driver_license) VALUES (2, 5);",
                    TestContext.Current.CancellationToken));
            });
    }

    /// <summary>
    /// The contrast case that proves the mechanism works when identity changes: a CHECK
    /// constraint's NAME does participate in identity, so renaming it (even with a different
    /// predicate) is seen as a drop plus a create, and the new predicate really is applied.
    /// </summary>
    [Fact]
    public async Task RenamedCheckConstraint_IsReconciled()
    {
        const string before = """
CREATE TABLE gauge
(
    id integer PRIMARY KEY,
    x  integer NOT NULL,
    CONSTRAINT ck_a CHECK (x > 0)
);
""";
        const string after = """
CREATE TABLE gauge
(
    id integer PRIMARY KEY,
    x  integer NOT NULL,
    CONSTRAINT ck_b CHECK (x > 10)
);
""";

        await RunScenarioAsync(
            before, after,
            seedSql: "INSERT INTO gauge (id, x) VALUES (1, 100);",
            afterOptions: new DeployOptions { DropObjectsNotInSource = true },
            assertAfterAsync: async (conn, result) =>
            {
                Assert.True(result.WasExecuted);
                Assert.False(string.IsNullOrWhiteSpace(result.Script),
                    "Renaming a constraint changes its identity and must generate a script.");

                // Only the new constraint remains, with the new predicate.
                var def = (string?)await ScalarAsync(conn, """
SELECT pg_get_constraintdef(oid) FROM pg_constraint
WHERE conrelid = 'gauge'::regclass AND conname = 'ck_b';
""");
                Assert.Equal("CHECK ((x > 10))", def);

                Assert.Equal(0L, await ScalarAsync(conn, """
SELECT count(*) FROM pg_constraint
WHERE conrelid = 'gauge'::regclass AND conname = 'ck_a';
"""));

                // The new, tighter predicate is genuinely enforced.
                await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                    "INSERT INTO gauge (id, x) VALUES (2, 5);",
                    TestContext.Current.CancellationToken));

                // The seeded row, which satisfies both predicates, survives.
                Assert.Equal(1L, await ScalarAsync(conn, "SELECT count(*) FROM gauge;"));
            });
    }

    /// <summary>
    /// Redefining a generated column's expression must rebuild the column so rows use the
    /// declared expression.
    /// </summary>
    [Fact]
    public async Task ChangedGeneratedColumnExpression_IsApplied()
    {
        const string before = """
CREATE TABLE tally
(
    id  integer PRIMARY KEY,
    x   integer NOT NULL,
    y   integer NOT NULL,
    sum integer GENERATED ALWAYS AS (x + y) STORED
);
""";
        const string after = """
CREATE TABLE tally
(
    id  integer PRIMARY KEY,
    x   integer NOT NULL,
    y   integer NOT NULL,
    sum integer GENERATED ALWAYS AS (x - y) STORED
);
""";

        await RunScenarioAsync(
            before, after,
            seedSql: "INSERT INTO tally (id, x, y) VALUES (1, 10, 3);",
            assertAfterAsync: async (conn, result) =>
            {
                Assert.False(string.IsNullOrWhiteSpace(result.Script),
                    "A changed generation expression should generate a non-empty script.");

                // The deployed expression must be the declared one.
                var expr = (string?)await ScalarAsync(conn, """
SELECT pg_get_expr(d.adbin, d.adrelid) FROM pg_attrdef d
JOIN pg_attribute a ON a.attrelid = d.adrelid AND a.attnum = d.adnum
WHERE d.adrelid = 'tally'::regclass AND a.attname = 'sum';
""");
                Assert.Equal("(x - y)", expr);

                // Rebuilding the column recomputes the seeded row under the new expression.
                Assert.Equal(7, await ScalarAsync(conn, "SELECT sum FROM tally WHERE id = 1;"));

                // A row inserted AFTER the deploy must use the new expression.
                await ExecuteAsync(conn,
                    "INSERT INTO tally (id, x, y) VALUES (2, 10, 3);",
                    TestContext.Current.CancellationToken);

                Assert.Equal(7, await ScalarAsync(conn, "SELECT sum FROM tally WHERE id = 2;"));
            });
    }

    /// <summary>
    /// Dropping a column's generated-ness must deploy as a real change: IsStored participates
    /// in identity, so an AlterDelta is produced, and the column must end up an ordinary one
    /// that accepts an explicitly supplied value.
    /// </summary>
    /// <remarks>
    /// Fixed by issue #158. The ALTER path emits clauses only for type, nullability and default,
    /// with no case for a change in generated-ness, so it returned an empty string that
    /// DacpacDeployerBase handed to Npgsql. PostgresTableDiffAnalyzer now treats gaining or
    /// losing generated-ness as a change the ALTER path cannot express, so it takes the rebuild
    /// path instead.
    /// </remarks>
    [Fact]
    public async Task DroppedGeneratedNess_IsApplied()
    {
        const string before = """
CREATE TABLE tally
(
    id  integer PRIMARY KEY,
    x   integer NOT NULL,
    y   integer NOT NULL,
    sum integer GENERATED ALWAYS AS (x + y) STORED
);
""";
        const string after = """
CREATE TABLE tally
(
    id  integer PRIMARY KEY,
    x   integer NOT NULL,
    y   integer NOT NULL,
    sum integer
);
""";

        await RunScenarioAsync(
            before, after,
            seedSql: "INSERT INTO tally (id, x, y) VALUES (1, 10, 3);",
            assertAfterAsync: async (conn, result) =>
            {
                Assert.False(string.IsNullOrWhiteSpace(result.Script),
                    "Dropping generated-ness should generate a non-empty script.");

                // The column must no longer be generated.
                var generated = (string?)await ScalarAsync(conn, """
SELECT attgenerated::text FROM pg_attribute
WHERE attrelid = 'tally'::regclass AND attname = 'sum';
""");
                Assert.Equal(string.Empty, generated);

                // An ordinary column accepts an explicitly supplied value, which a generated
                // column would reject.
                await ExecuteAsync(conn,
                    "INSERT INTO tally (id, x, y, sum) VALUES (2, 1, 1, 99);",
                    TestContext.Current.CancellationToken);

                Assert.Equal(99, await ScalarAsync(conn, "SELECT sum FROM tally WHERE id = 2;"));
            });
    }

    /// <summary>
    /// Regression guard that the working path is untouched by the gaps above: adding a named
    /// CHECK to a table that already exists is a genuine identity change and must deploy as a
    /// standalone ALTER TABLE ... ADD CONSTRAINT that is really enforced.
    /// </summary>
    [Fact]
    public async Task AddedCheckToExistingTable_StillWorks()
    {
        const string before = """
CREATE TABLE meter
(
    id integer PRIMARY KEY,
    x  integer NOT NULL
);
""";
        const string after = """
CREATE TABLE meter
(
    id integer PRIMARY KEY,
    x  integer NOT NULL,
    CONSTRAINT ck_meter_positive CHECK (x > 0)
);
""";

        await RunScenarioAsync(
            before, after,
            seedSql: "INSERT INTO meter (id, x) VALUES (1, 5);",
            assertAfterAsync: async (conn, result) =>
            {
                Assert.True(result.WasExecuted);
                Assert.False(string.IsNullOrWhiteSpace(result.Script),
                    "Adding a constraint to an existing table must generate a script.");

                var def = (string?)await ScalarAsync(conn, """
SELECT pg_get_constraintdef(oid) FROM pg_constraint
WHERE conrelid = 'meter'::regclass AND conname = 'ck_meter_positive';
""");
                Assert.Equal("CHECK ((x > 0))", def);

                // The seeded row survives and the constraint is enforced from here on.
                Assert.Equal(1L, await ScalarAsync(conn, "SELECT count(*) FROM meter;"));

                await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                    "INSERT INTO meter (id, x) VALUES (2, 0);",
                    TestContext.Current.CancellationToken));
            });
    }

    // Deploys `before`, seeds data, deploys `after` to the same database, then hands the
    // caller both a connection and the second deploy's result. Unlike the AlterTableTest
    // helper this does NOT assert that a script was produced — several of these scenarios
    // are expected to produce none, which is exactly what is being characterized.
    private async Task RunScenarioAsync(
        string before,
        string after,
        string seedSql,
        Func<NpgsqlConnection, Provider.Postgres.DeployResult, Task> assertAfterAsync,
        DeployOptions? afterOptions = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-expression-redefinition");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_exprredef_{Guid.NewGuid():n}";
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

                await using var conn = await OpenAsync(targetDbName, ct);
                await assertAfterAsync(conn, result);
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

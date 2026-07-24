using Npgsql;
using Squill.Core;
using Squill.Dacpac;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.UniqueAndSerialTest;

// End-to-end coverage against real Postgres for issue #121: UNIQUE constraints (column and
// table level), the serial types, and adding an index or unique constraint to a table that
// already exists. Each scenario deploys through the same DacpacDeployer code path the CLI
// uses and then verifies the deployed database's model hash-matches the DACPAC's — which is
// what proves the parser-built model and the extracted model agree.
public class PostgresUniqueAndSerialTest : PostgresIntegrationTestBase
{
    [Fact]
    public async Task UniqueConstraints_RoundTripAndEnforceUniqueness()
    {
        const string schema = """
CREATE TABLE users
(
    user_id   integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email     varchar(255) NOT NULL UNIQUE,
    tenant_id integer NOT NULL,
    username  varchar(100) NOT NULL,
    CONSTRAINT uq_users_tenant_username UNIQUE (tenant_id, username)
);
""";

        await RunDeployScenarioAsync(schema, async conn =>
        {
            // Both constraints must exist in pg_constraint as real UNIQUE constraints
            // (contype 'u') rather than as bare unique indexes.
            var inlineName = (string?)await ScalarAsync(conn, """
SELECT conname FROM pg_constraint
WHERE conrelid = 'users'::regclass AND contype = 'u'
  AND array_length(conkey, 1) = 1;
""");
            Assert.Equal("users_email_key", inlineName);

            var compositeName = (string?)await ScalarAsync(conn, """
SELECT conname FROM pg_constraint
WHERE conrelid = 'users'::regclass AND contype = 'u'
  AND array_length(conkey, 1) = 2;
""");
            Assert.Equal("uq_users_tenant_username", compositeName);

            // And they must actually be enforced.
            await ExecuteAsync(conn,
                "INSERT INTO users (email, tenant_id, username) VALUES ('a@example.com', 1, 'alice');",
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO users (email, tenant_id, username) VALUES ('a@example.com', 2, 'bob');",
                TestContext.Current.CancellationToken));

            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO users (email, tenant_id, username) VALUES ('c@example.com', 1, 'alice');",
                TestContext.Current.CancellationToken));

            // A different tenant may reuse the username — the constraint is composite.
            await ExecuteAsync(conn,
                "INSERT INTO users (email, tenant_id, username) VALUES ('d@example.com', 2, 'alice');",
                TestContext.Current.CancellationToken);
        });
    }

    /// <summary>
    /// Postgres requires a foreign key to be backed by an exact unique column set; a UNIQUE
    /// constraint provides one, so the FK must deploy successfully.
    /// </summary>
    [Fact]
    public async Task ForeignKey_ReferencingAUniqueColumn_Deploys()
    {
        const string schema = """
CREATE TABLE users
(
    user_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email   varchar(255) NOT NULL UNIQUE
);

CREATE TABLE logins
(
    login_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email    varchar(255) NOT NULL REFERENCES users (email)
);
""";

        await RunDeployScenarioAsync(schema, async conn =>
        {
            await ExecuteAsync(conn,
                "INSERT INTO users (email) VALUES ('a@example.com');",
                TestContext.Current.CancellationToken);

            await ExecuteAsync(conn,
                "INSERT INTO logins (email) VALUES ('a@example.com');",
                TestContext.Current.CancellationToken);

            // The FK must be enforced against the unique column.
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO logins (email) VALUES ('nobody@example.com');",
                TestContext.Current.CancellationToken));
        });
    }

    [Theory]
    [InlineData("smallserial", "smallint")]
    [InlineData("serial", "integer")]
    [InlineData("bigserial", "bigint")]
    public async Task SerialColumn_RoundTripsAsIdentityOverUnderlyingType(
        string serialType, string expectedType)
    {
        var schema = $"""
CREATE TABLE widgets
(
    widget_id {serialType} PRIMARY KEY,
    name      text NOT NULL
);
""";

        await RunDeployScenarioAsync(schema, async conn =>
        {
            // The deployed column must have the underlying integer type, not a type
            // literally named "serial" (which does not exist in Postgres).
            var dataType = (string?)await ScalarAsync(conn, """
SELECT data_type FROM information_schema.columns
WHERE table_name = 'widgets' AND column_name = 'widget_id';
""");
            Assert.Equal(expectedType, dataType);

            // It is lowered to an identity column, so it is NOT NULL and auto-populates.
            var isIdentity = (string?)await ScalarAsync(conn, """
SELECT is_identity FROM information_schema.columns
WHERE table_name = 'widgets' AND column_name = 'widget_id';
""");
            Assert.Equal("YES", isIdentity);

            await ExecuteAsync(conn, "INSERT INTO widgets (name) VALUES ('first'), ('second');",
                TestContext.Current.CancellationToken);

            var maxId = await ScalarAsync(conn, "SELECT max(widget_id) FROM widgets;");
            Assert.Equal(2, Convert.ToInt32(maxId));
        });
    }

    /// <summary>
    /// A unique constraint on a table in a non-public schema must round-trip too: the
    /// constraint name the parser predicts has to match what the extractor reads back, and
    /// the ALTER TABLE that adds one has to be schema-qualified.
    /// </summary>
    [Fact]
    public async Task UniqueConstraint_InNonPublicSchema_RoundTrips()
    {
        const string schema = """
CREATE SCHEMA staging;

CREATE TABLE staging.account
(
    account_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email      varchar(255) NOT NULL UNIQUE
);
""";

        await RunDeployScenarioAsync(schema, async conn =>
        {
            var conname = (string?)await ScalarAsync(conn, """
SELECT conname FROM pg_constraint
WHERE conrelid = 'staging.account'::regclass AND contype = 'u';
""");
            Assert.Equal("account_email_key", conname);

            await ExecuteAsync(conn,
                "INSERT INTO staging.account (email) VALUES ('a@example.com');",
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO staging.account (email) VALUES ('a@example.com');",
                TestContext.Current.CancellationToken));
        });
    }

    /// <summary>
    /// Adding a unique constraint to an existing table in a non-public schema must emit a
    /// schema-qualified ALTER TABLE, or it would resolve against the search_path instead.
    /// </summary>
    [Fact]
    public async Task AddUniqueConstraint_ToExistingTableInNonPublicSchema_IsSchemaQualified()
    {
        const string before = """
CREATE SCHEMA staging;

CREATE TABLE staging.account
(
    account_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email      varchar(255) NOT NULL
);
""";
        const string after = """
CREATE SCHEMA staging;

CREATE TABLE staging.account
(
    account_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email      varchar(255) NOT NULL,
    CONSTRAINT uq_account_email UNIQUE (email)
);
""";

        await RunIncrementalDeployScenarioAsync(
            before, after,
            seedSql: "INSERT INTO staging.account (email) VALUES ('a@example.com');",
            assertAfterAsync: async conn =>
            {
                var conname = (string?)await ScalarAsync(conn, """
SELECT conname FROM pg_constraint
WHERE conrelid = 'staging.account'::regclass AND contype = 'u';
""");
                Assert.Equal("uq_account_email", conname);

                var count = await ScalarAsync(conn, "SELECT count(*) FROM staging.account;");
                Assert.Equal(1L, count);
            });
    }

    /// <summary>
    /// Adding an index to a table that already exists has no CREATE TABLE to carry it, so it
    /// must be deployed as a standalone CREATE INDEX. This previously produced no delta at
    /// all, silently leaving the index uncreated.
    /// </summary>
    [Fact]
    public async Task AddIndexToExistingTable_CreatesIndex_AndPreservesData()
    {
        const string before = """
CREATE TABLE film
(
    film_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title   varchar(255) NOT NULL
);
""";
        const string after = """
CREATE TABLE film
(
    film_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title   varchar(255) NOT NULL
);

CREATE INDEX idx_film_title ON film (title);
""";

        await RunIncrementalDeployScenarioAsync(
            before, after,
            seedSql: "INSERT INTO film (title) VALUES ('Alpha'), ('Beta');",
            assertAfterAsync: async conn =>
            {
                var indexDef = (string?)await ScalarAsync(conn, """
SELECT indexdef FROM pg_indexes
WHERE tablename = 'film' AND indexname = 'idx_film_title';
""");
                Assert.NotNull(indexDef);
                Assert.Contains("title", indexDef);

                // The pre-existing rows must survive.
                var count = await ScalarAsync(conn, "SELECT count(*) FROM film;");
                Assert.Equal(2L, count);
            });
    }

    [Fact]
    public async Task AddUniqueConstraintToExistingTable_AddsConstraint_AndPreservesData()
    {
        const string before = """
CREATE TABLE account
(
    account_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email      varchar(255) NOT NULL
);
""";
        const string after = """
CREATE TABLE account
(
    account_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email      varchar(255) NOT NULL,
    CONSTRAINT uq_account_email UNIQUE (email)
);
""";

        await RunIncrementalDeployScenarioAsync(
            before, after,
            seedSql: "INSERT INTO account (email) VALUES ('a@example.com');",
            assertAfterAsync: async conn =>
            {
                var conname = (string?)await ScalarAsync(conn, """
SELECT conname FROM pg_constraint
WHERE conrelid = 'account'::regclass AND contype = 'u';
""");
                Assert.Equal("uq_account_email", conname);

                // The seeded row must survive, and the constraint must now be enforced.
                var count = await ScalarAsync(conn, "SELECT count(*) FROM account;");
                Assert.Equal(1L, count);

                await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                    "INSERT INTO account (email) VALUES ('a@example.com');",
                    TestContext.Current.CancellationToken));
            });
    }

    // Deploys a schema to a fresh database, verifies the deployed database's model
    // hash-matches the DACPAC's, then runs the caller's assertions against it.
    private async Task RunDeployScenarioAsync(
        string schema, Func<NpgsqlConnection, Task> assertAsync)
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-unique-serial-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_unique_serial_{Guid.NewGuid():n}";
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
        var tempDir = Directory.CreateTempSubdirectory("squill-unique-serial-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_unique_serial_{Guid.NewGuid():n}";
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
                    "Adding an object to an existing table should generate a non-empty script.");

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
            (_, dacpacModel) = await DacpacSerializer.Deserialize(stream, ct);
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

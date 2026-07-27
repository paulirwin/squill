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
        var model = (await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(ct)).Model;

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

    /// <summary>
    /// Issue #124: a <c>now()</c> function default — the one Pagila's <c>last_update</c>
    /// columns use — must round-trip too. Postgres stores such a default with the spelling it
    /// was written with, normalizing case and an explicit <c>pg_catalog.</c> prefix, so the
    /// parsed and extracted models must agree on one canonical token or every deploy would see
    /// a phantom column change. A serial column is included deliberately: its default is a
    /// <c>nextval(...)</c> call that must stay unmodeled.
    /// </summary>
    [Fact]
    public async Task FunctionDefaults_RoundTrip_ModelHashesMatchAfterPublish()
    {
        const string schema = """
CREATE TABLE audit_entry
(
    id          serial PRIMARY KEY,
    created_at  timestamp NOT NULL DEFAULT now(),
    modified_at timestamp NOT NULL DEFAULT NOW(),
    catalog_at  timestamp NOT NULL DEFAULT pg_catalog.now(),
    label       varchar(20) NOT NULL DEFAULT 'new'
);
""";

        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("AuditEntry.sql", FileKind.Compile, schema));
        var buildResult = await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(ct);

        // The whole point of the issue: these defaults no longer warn as unmodeled.
        Assert.Empty(buildResult.Warnings);

        var model = buildResult.Model;
        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, model, emptyModel), ct);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(ct);

            Assert.True(
                HashUtility.HashesEqual(model.Hash, publishedModel.Hash),
                "Parsed and extracted model hashes do not match — a function default did not "
                + "canonicalize identically on both sides.");

            // Redeploying the same source must be a no-op; a default that canonicalized
            // differently on the two sides would show up here as a perpetual diff.
            var redeploy = SchemaCompare.Compare(provider, model, publishedModel);
            Assert.Empty(redeploy.Deltas);

            // The default must actually apply on INSERT.
            await using var conn = await OpenAsync(testDb.Name, ct);
            await ExecuteAsync(conn, "INSERT INTO audit_entry DEFAULT VALUES;", ct);

            Assert.NotNull(await ScalarAsync(conn, "SELECT created_at FROM audit_entry;"));
            Assert.Equal("new", await ScalarAsync(conn, "SELECT label FROM audit_entry;"));
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    /// <summary>
    /// Issue #139: a signed numeric default — <c>DEFAULT -5</c>, <c>DEFAULT -1.5</c>,
    /// <c>DEFAULT +5</c> — must round-trip. These previously failed the build outright, because
    /// the parser threw on a leading sign in <c>b_expr</c> position. The two signs are stored
    /// differently by Postgres (<c>-5</c> as the cast <c>'-5'::integer</c>, <c>+5</c> as the
    /// parenthesized <c>(+ 5)</c>), so both spellings have to reduce to the same canonical token
    /// the parser produces or a redeploy would see a phantom column change. A <c>CHECK</c> with
    /// a negative literal is included because it takes the same grammar path.
    /// </summary>
    [Fact]
    public async Task SignedNumericDefaults_RoundTrip_ModelHashesMatchAfterPublish()
    {
        const string schema = """
CREATE TABLE reading
(
    id       integer PRIMARY KEY,
    offset_c integer NOT NULL DEFAULT -5,
    delta    numeric(6, 2) NOT NULL DEFAULT -1.5,
    boost    integer NOT NULL DEFAULT +5,
    level    integer NOT NULL DEFAULT 0 CHECK (level > -1)
);
""";

        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Reading.sql", FileKind.Compile, schema));
        var buildResult = await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(ct);

        // The whole point of the issue: these no longer fail the build, nor warn as unmodeled.
        Assert.Empty(buildResult.Warnings);

        var model = buildResult.Model;
        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, model, emptyModel), ct);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(ct);

            Assert.True(
                HashUtility.HashesEqual(model.Hash, publishedModel.Hash),
                "Parsed and extracted model hashes do not match — a signed default did not "
                + "canonicalize identically on both sides.");

            // Redeploying the same source must be a no-op; a sign that canonicalized
            // differently on the two sides would show up here as a perpetual diff.
            var redeploy = SchemaCompare.Compare(provider, model, publishedModel);
            Assert.Empty(redeploy.Deltas);

            // The defaults must actually apply on INSERT, with the sign intact.
            await using var conn = await OpenAsync(testDb.Name, ct);
            await ExecuteAsync(conn, "INSERT INTO reading (id) VALUES (1);", ct);

            Assert.Equal(-5, await ScalarAsync(conn, "SELECT offset_c FROM reading WHERE id = 1;"));
            Assert.Equal(-1.50m, await ScalarAsync(conn, "SELECT delta FROM reading WHERE id = 1;"));
            Assert.Equal(5, await ScalarAsync(conn, "SELECT boost FROM reading WHERE id = 1;"));
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    /// <summary>
    /// Issue #140: the niladic keyword defaults — <c>CURRENT_TIMESTAMP</c>, <c>CURRENT_DATE</c>,
    /// <c>CURRENT_TIME</c>, <c>LOCALTIMESTAMP</c> — could not be written in Postgres source at
    /// all before <c>func_expr_common_subexpr</c> was implemented; the build threw.
    ///
    /// The round trip is the real check. Postgres stores each keyword with the spelling it was
    /// given and does *not* rewrite it into <c>now()</c>, so a keyword default and a
    /// <c>now()</c> default must canonicalize to different tokens. Modeling them as the same
    /// thing would make one of the two re-diff on every deploy, which is what the redeploy
    /// assertion below catches.
    /// </summary>
    [Fact]
    public async Task KeywordDefaults_RoundTrip_ModelHashesMatchAfterPublish()
    {
        const string schema = """
CREATE TABLE keyword_default
(
    id           integer PRIMARY KEY,
    stamped_at   timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
    lowered_at   timestamp NOT NULL DEFAULT current_timestamp,
    local_at     timestamp NOT NULL DEFAULT LOCALTIMESTAMP,
    stamped_on   date NOT NULL DEFAULT CURRENT_DATE,
    stamped_time time NOT NULL DEFAULT CURRENT_TIME,
    -- A now() default alongside, to prove the two spellings stay distinct rather than
    -- being folded into one canonical token.
    created_at   timestamp NOT NULL DEFAULT now()
);
""";

        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("KeywordDefault.sql", FileKind.Compile, schema));
        var buildResult = await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(ct);

        Assert.Empty(buildResult.Warnings);

        var model = buildResult.Model;
        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, model, emptyModel), ct);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(ct);

            Assert.True(
                HashUtility.HashesEqual(model.Hash, publishedModel.Hash),
                "Parsed and extracted model hashes do not match — a keyword default did not "
                + "canonicalize identically on both sides.");

            // A keyword default that canonicalized differently on the two sides would show up
            // here as a perpetual diff.
            var redeploy = SchemaCompare.Compare(provider, model, publishedModel);
            Assert.Empty(redeploy.Deltas);

            // The defaults must actually apply on INSERT.
            await using var conn = await OpenAsync(testDb.Name, ct);
            await ExecuteAsync(conn, "INSERT INTO keyword_default (id) VALUES (1);", ct);

            Assert.NotNull(await ScalarAsync(conn, "SELECT stamped_at FROM keyword_default;"));
            Assert.NotNull(await ScalarAsync(conn, "SELECT local_at FROM keyword_default;"));
            Assert.NotNull(await ScalarAsync(conn, "SELECT stamped_on FROM keyword_default;"));
            Assert.NotNull(await ScalarAsync(conn, "SELECT stamped_time FROM keyword_default;"));
            Assert.NotNull(await ScalarAsync(conn, "SELECT created_at FROM keyword_default;"));
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    /// <summary>
    /// Issue #140: the same constructs in a <c>CHECK</c> predicate, which is the other position
    /// where they show up most. A CHECK is carried into the model as rendered SQL text, so this
    /// proves <c>ExpressionSqlRenderer</c> emits valid, executable Postgres for each form — not
    /// just something that round-trips through our own code.
    /// </summary>
    [Fact]
    public async Task CommonSubexprInCheck_RoundTrips_AndEnforcesConstraint()
    {
        const string schema = """
CREATE TABLE reservation
(
    id         integer PRIMARY KEY,
    code       varchar(20) NOT NULL,
    quantity   integer NOT NULL,
    spare      integer,
    booked_on  date NOT NULL,
    CONSTRAINT ck_booked_not_future CHECK (booked_on <= CURRENT_DATE),
    CONSTRAINT ck_code_prefix CHECK (SUBSTRING(code FROM 1 FOR 2) = 'RS'),
    CONSTRAINT ck_code_trimmed CHECK (TRIM(BOTH ' ' FROM code) = code),
    CONSTRAINT ck_quantity_positive CHECK (COALESCE(spare, quantity) > 0),
    CONSTRAINT ck_quantity_cast CHECK (CAST(quantity AS bigint) < 1000),
    CONSTRAINT ck_booked_year CHECK (EXTRACT(YEAR FROM booked_on) >= 2000)
);
""";

        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Reservation.sql", FileKind.Compile, schema));
        var buildResult = await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(ct);

        var model = buildResult.Model;
        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(ct);

            // Publishing at all proves the rendered predicates are valid, executable Postgres.
            await testDb.PublishAsync(SchemaCompare.Compare(provider, model, emptyModel), ct);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(ct);
            var redeploy = SchemaCompare.Compare(provider, model, publishedModel);
            Assert.Empty(redeploy.Deltas);

            await using var conn = await OpenAsync(testDb.Name, ct);

            // A conforming row is accepted.
            await ExecuteAsync(
                conn,
                "INSERT INTO reservation (id, code, quantity, booked_on) "
                + "VALUES (1, 'RS-001', 5, DATE '2020-01-01');",
                ct);

            Assert.Equal(1, await ScalarAsync(conn, "SELECT COUNT(*)::int FROM reservation;"));

            // Each constraint actually bites.
            await AssertCheckViolationAsync(
                conn,
                "INSERT INTO reservation (id, code, quantity, booked_on) "
                + "VALUES (2, 'XX-001', 5, DATE '2020-01-01');",
                "ck_code_prefix", ct);

            await AssertCheckViolationAsync(
                conn,
                "INSERT INTO reservation (id, code, quantity, booked_on) "
                + "VALUES (3, 'RS-002', 0, DATE '2020-01-01');",
                "ck_quantity_positive", ct);

            await AssertCheckViolationAsync(
                conn,
                "INSERT INTO reservation (id, code, quantity, booked_on) "
                + "VALUES (4, 'RS-003', 5, DATE '1999-01-01');",
                "ck_booked_year", ct);
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    private static async Task AssertCheckViolationAsync(
        NpgsqlConnection conn, string sql, string constraintName, CancellationToken ct)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn, sql, ct));

        // 23514 is check_violation.
        Assert.Equal("23514", ex.SqlState);
        Assert.Equal(constraintName, ex.ConstraintName);
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

                Assert.Equal(ModelAssertions.ElementHashMultiset(afterModel), ModelAssertions.ElementHashMultiset(deployedModel));

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

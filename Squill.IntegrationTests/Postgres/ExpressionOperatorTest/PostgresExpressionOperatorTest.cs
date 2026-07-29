using Npgsql;
using Squill.Core;
using Squill.Dacpac;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.ExpressionOperatorTest;

// End-to-end coverage against real Postgres for issue #141: the expression operators the
// parser previously rejected — LIKE/ILIKE/SIMILAR TO, BETWEEN, COLLATE, AT TIME ZONE, the
// caret operator, and typed literals. Each is valid PostgreSQL that used to fail the build.
//
// Parsing them is only half the story. A CHECK predicate is carried into the model as text,
// so an operator that parses but renders back differently would deploy a predicate meaning
// something else — and Postgres rewrites stored CHECK text, so a predicate that does not
// survive the rewrite re-diffs on every deploy. Each scenario therefore deploys through the
// same DacpacDeployer path the CLI uses, verifies the deployed database's model hash-matches
// the DACPAC's, and checks the constraint actually rejects the rows it should.
public class PostgresExpressionOperatorTest : PostgresIntegrationTestBase
{
    /// <summary>
    /// LIKE and BETWEEN are the two most likely to appear in practice, both in CHECK
    /// constraints. BETWEEN is also the one the grammar mis-associates, so this is the
    /// end-to-end proof that the visitor's reassociation produces the right predicate.
    /// </summary>
    [Fact]
    public async Task CheckConstraints_WithPatternAndRangeOperators_RoundTripAndAreEnforced()
    {
        const string schema = """
CREATE TABLE account
(
    account_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code       text    NOT NULL,
    email      text    NOT NULL,
    score      integer NOT NULL,
    CONSTRAINT ck_code_prefix CHECK (code LIKE 'AC-%'),
    CONSTRAINT ck_email_has_at CHECK (email LIKE '%@%'),
    CONSTRAINT ck_score_range CHECK (score BETWEEN 0 AND 100)
);
""";

        await RunDeployScenarioAsync(schema, async conn =>
        {
            Assert.Equal(
                ["ck_code_prefix", "ck_email_has_at", "ck_score_range"],
                await CheckConstraintNamesAsync(conn, "account"));

            var ct = TestContext.Current.CancellationToken;

            // A row satisfying all three.
            await ExecuteAsync(conn,
                "INSERT INTO account (code, email, score) VALUES ('AC-1', 'a@b.com', 50);", ct);

            // code LIKE 'AC-%'
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO account (code, email, score) VALUES ('XX-1', 'a@b.com', 50);", ct));

            // email LIKE '%@%'
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO account (code, email, score) VALUES ('AC-1', 'nope', 50);", ct));

            // score BETWEEN 0 AND 100 — both ends, which is what pins the reassociation:
            // had the upper bound been dropped the predicate would have been `score >= 0`
            // and 101 would have been accepted.
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO account (code, email, score) VALUES ('AC-1', 'a@b.com', -1);", ct));

            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO account (code, email, score) VALUES ('AC-1', 'a@b.com', 101);", ct));

            // And the boundaries themselves are inclusive.
            await ExecuteAsync(conn,
                "INSERT INTO account (code, email, score) VALUES ('AC-2', 'a@b.com', 0);", ct);
            await ExecuteAsync(conn,
                "INSERT INTO account (code, email, score) VALUES ('AC-3', 'a@b.com', 100);", ct);
        });
    }

    /// <summary>
    /// The negated and case-insensitive spellings, a BETWEEN alongside a real conjunct (where
    /// the visitor has to take exactly one operand as the bound and leave the rest), and an
    /// ESCAPE clause — which changes what the pattern matches and so must not be dropped.
    /// </summary>
    [Fact]
    public async Task CheckConstraints_WithNegatedAndEscapedOperators_RoundTripAndAreEnforced()
    {
        const string schema = """
CREATE TABLE document
(
    document_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    title       text    NOT NULL,
    kind        text    NOT NULL,
    pages       integer NOT NULL,
    CONSTRAINT ck_title_not_draft CHECK (title NOT LIKE 'DRAFT %'),
    CONSTRAINT ck_kind_case_insensitive CHECK (kind ILIKE 'report%'),
    CONSTRAINT ck_title_literal_percent CHECK (title LIKE '%!%%' ESCAPE '!'),
    CONSTRAINT ck_pages_and_kind CHECK (pages BETWEEN 1 AND 500 AND kind <> '')
);
""";

        await RunDeployScenarioAsync(schema, async conn =>
        {
            var ct = TestContext.Current.CancellationToken;

            // Satisfies all four: not a draft, kind starts with "report" (any case), the
            // title contains a literal '%', and pages is in range with a non-empty kind.
            await ExecuteAsync(conn,
                "INSERT INTO document (title, kind, pages) VALUES ('50% off', 'RePoRt-a', 10);", ct);

            // title NOT LIKE 'DRAFT %'
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO document (title, kind, pages) VALUES ('DRAFT 50%', 'report', 10);", ct));

            // kind ILIKE 'report%'
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO document (title, kind, pages) VALUES ('50%', 'memo', 10);", ct));

            // title LIKE '%!%%' ESCAPE '!' — the escaped '%' is a literal, so a title
            // without one fails even though it is otherwise fine. Had ESCAPE been dropped
            // the pattern would have been '%%%', which matches anything.
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO document (title, kind, pages) VALUES ('no percent', 'report', 10);", ct));

            // pages BETWEEN 1 AND 500 AND kind <> '' — the upper bound must belong to the
            // BETWEEN and the trailing comparison must survive as its own conjunct.
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn,
                "INSERT INTO document (title, kind, pages) VALUES ('50%', 'report', 501);", ct));
        });
    }

    /// <summary>
    /// COLLATE, AT TIME ZONE, the caret operator and a typed literal. The caret and
    /// AT TIME ZONE are the two whose grammar rules over-capture to the right, so a CHECK
    /// combining one with a comparison is the end-to-end proof that the rebalancing produces
    /// the predicate Postgres itself would have parsed.
    /// </summary>
    [Fact]
    public async Task CheckConstraints_WithCollateTimeZoneAndCaret_RoundTripAndAreEnforced()
    {
        const string schema = """
CREATE TABLE reading
(
    reading_id  integer   NOT NULL PRIMARY KEY,
    label       text      NOT NULL,
    taken_at    timestamp NOT NULL,
    magnitude   numeric   NOT NULL,
    CONSTRAINT ck_label_collated CHECK (label COLLATE "C" > 'A'),
    CONSTRAINT ck_taken_at_after CHECK (taken_at AT TIME ZONE 'UTC' > timestamp '2000-01-01'),
    CONSTRAINT ck_magnitude_squared CHECK (magnitude ^ 2 < 10000)
);
""";

        await RunDeployScenarioAsync(schema, async conn =>
        {
            var ct = TestContext.Current.CancellationToken;

            await ExecuteAsync(conn, """
INSERT INTO reading (reading_id, label, taken_at, magnitude)
VALUES (1, 'beta', '2020-06-01 12:00:00', 5);
""", ct);

            // label COLLATE "C" > 'A'
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn, """
INSERT INTO reading (reading_id, label, taken_at, magnitude)
VALUES (2, 'A', '2020-06-01 12:00:00', 5);
""", ct));

            // taken_at AT TIME ZONE 'UTC' > timestamp '2000-01-01' — this is also the case
            // that would break if the typed literal lost its `timestamp` prefix.
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn, """
INSERT INTO reading (reading_id, label, taken_at, magnitude)
VALUES (3, 'beta', '1999-01-01 12:00:00', 5);
""", ct));

            // magnitude ^ 2 < 10000 — had the caret captured the comparison the predicate
            // would have been `magnitude ^ (2 < 10000)`, which is not even type-correct.
            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(conn, """
INSERT INTO reading (reading_id, label, taken_at, magnitude)
VALUES (4, 'beta', '2020-06-01 12:00:00', 500);
""", ct));
        });
    }

    /// <summary>
    /// Deploying the same schema twice must be a no-op. This is the regression that matters
    /// most: Postgres rewrites a stored CHECK's text, so if the declared predicate did not
    /// reduce to the same model as the one read back, every one of these constraints would
    /// re-diff on each deploy.
    /// </summary>
    [Fact]
    public async Task RedeployingOperatorChecks_ProducesNoChanges()
    {
        const string schema = """
CREATE TABLE measurement
(
    measurement_id integer   NOT NULL PRIMARY KEY,
    code           text      NOT NULL,
    taken_at       timestamp NOT NULL,
    score          integer   NOT NULL,
    magnitude      numeric   NOT NULL,
    CONSTRAINT ck_code_prefix CHECK (code LIKE 'M-%'),
    CONSTRAINT ck_code_not_draft CHECK (code NOT LIKE 'DRAFT%'),
    CONSTRAINT ck_code_escaped CHECK (code LIKE '%!%%' ESCAPE '!'),
    CONSTRAINT ck_score_range CHECK (score BETWEEN 0 AND 100),
    CONSTRAINT ck_score_not_range CHECK (score NOT BETWEEN 200 AND 300),
    CONSTRAINT ck_collated CHECK (code COLLATE "C" > 'A'),
    CONSTRAINT ck_time_zone CHECK (taken_at AT TIME ZONE 'UTC' > timestamp '2000-01-01'),
    CONSTRAINT ck_caret CHECK (magnitude ^ 2 < 10000)
);
""";

        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-expression-operator-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_expression_operator_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var dacpac = await BuildDacpacAsync(tempDir.FullName, "schema", schema, ct);

                await DacpacDeployer.DeployFromFileAsync(
                    dacpac, ConnectionString, targetDbName, cancellationToken: ct);

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

    /// <summary>
    /// A partial index whose predicate uses these operators — the other position where a
    /// parsed expression is carried into the model as text. The rendered predicate must be
    /// valid, correctly-associated SQL that Postgres accepts and applies as written.
    ///
    /// Note this deploys and inspects the index directly rather than going through
    /// <see cref="RunDeployScenarioAsync"/>: an index predicate is stored as the text the
    /// parser rendered, while extraction reads back Postgres's own rewriting of it
    /// (<c>LIKE</c> becomes <c>~~</c>, <c>BETWEEN</c> expands to <c>&gt;=</c>/<c>&lt;=</c>),
    /// so the two models do not hash-match. Canonicalizing an arbitrary predicate to
    /// <c>pg_get_expr</c>'s spelling is a separate problem from parsing these operators and
    /// is not attempted here — the same gap already applies to every partial index built
    /// through the parser path, which is why the existing round-trip test in
    /// PostgresIndexRoundTripTest builds both sides via TemporaryDatabaseModelBuilder.
    /// </summary>
    [Fact]
    public async Task PartialIndex_WithOperatorPredicate_DeploysAndIsApplied()
    {
        const string schema = """
CREATE TABLE contact
(
    contact_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email      text    NOT NULL,
    score      integer NOT NULL
);

CREATE INDEX ix_contact_active ON contact (email)
    WHERE email LIKE '%@example.com' AND score BETWEEN 1 AND 10;
""";

        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-expression-operator-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_expression_operator_{Guid.NewGuid():n}";
            var createdDb = await provider.CreateDatabaseAsync(targetDbName, ct);

            try
            {
                var dacpac = await BuildDacpacAsync(tempDir.FullName, "schema", schema, ct);

                await DacpacDeployer.DeployFromFileAsync(
                    dacpac, ConnectionString, targetDbName, cancellationToken: ct);

                await using var conn = await OpenAsync(targetDbName, ct);

                var indexdef = (string?)await ScalarAsync(conn, """
SELECT indexdef FROM pg_indexes
WHERE tablename = 'contact' AND indexname = 'ix_contact_active';
""");

                Assert.NotNull(indexdef);

                // Postgres rewrites the predicate's spelling, so assert on what it means
                // rather than on the declared text. Both BETWEEN bounds must be present —
                // a dropped upper bound is exactly what the grammar's mis-association
                // would have produced.
                Assert.Contains("~~", indexdef);
                Assert.Contains(">= 1", indexdef);
                Assert.Contains("<= 10", indexdef);
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
        var tempDir = Directory.CreateTempSubdirectory("squill-expression-operator-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_expression_operator_{Guid.NewGuid():n}";
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

    // The deployed database's model must agree with the DACPAC's, which is what proves the
    // parser-built and database-extracted models agree on these constructs.
    //
    // Agreement is asserted as "comparing them produces nothing to deploy" rather than as raw
    // hash equality. One of these predicates — LIKE with an ESCAPE — is stored by PostgreSQL as a
    // like_escape() call, a spelling the normalizer deliberately refuses (issue #171), so only
    // the extracted side carries a canonical form. SchemaCompare handles that by dropping a
    // one-sided canonical form (issue #156); a bare hash comparison cannot, and would report a
    // difference for two models that are in fact equivalent.
    private static async Task AssertModelsMatchAsync(
        IDatabaseProvider provider, IDatabase createdDb, string dacpacPath, CancellationToken ct)
    {
        Model dacpacModel;
        await using (var stream = File.OpenRead(dacpacPath))
        {
            (_, dacpacModel) = await DacpacSerializer.Deserialize(
                stream, provider.DependencyAnalyzer, ct);
        }

        var deployedModel = await provider
            .CreateDatabaseModelBuilder(createdDb)
            .ExtractModelAsync(ct);

        var comparison = SchemaCompare.Compare(provider, dacpacModel, deployedModel);

        Assert.True(comparison.Deltas.Count == 0,
            "The deployed database must already match the DACPAC, but comparing them produced: "
            + string.Join(", ", comparison.Deltas.Select(d => d.ToString())));
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

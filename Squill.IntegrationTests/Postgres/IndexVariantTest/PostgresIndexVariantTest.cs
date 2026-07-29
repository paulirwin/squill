using Npgsql;
using Squill.Core;
using Squill.Dacpac;
using Squill.PostgresParser;
using Squill.Provider.Postgres;
using Squill.TestFramework;

namespace Squill.IntegrationTests.Postgres.IndexVariantTest;

// CREATE INDEX variants beyond a plain column list, plus the one index-level ALTER (issue #137,
// EF Core coverage parity). Each is valid, executable Postgres a user could reasonably write
// declaratively — every test first proves that by running the DDL against the container before
// asserting Squill agrees.
//
// These once failed in three different components — the parser's Indexstmt visitor, its
// IndexElem visitor, and ParserWorkspaceModelBuilder's element mapping — and are kept distinct
// so a fix to one is not mistaken for a fix to all; the two expression-index spellings in
// particular take different grammar alternatives and so are asserted separately. All are
// supported as of #160.
//
// Every scenario asserts the index both deploys as declared *and* round-trips, since a variant
// modeled on only one side deploys correctly once and then re-diffs forever.
public class PostgresIndexVariantTest : PostgresIntegrationTestBase
{
    private const string PeopleTable = """
CREATE TABLE people
(
    id         integer PRIMARY KEY,
    name       text,
    age        integer,
    first_name text,
    last_name  text
);
""";

    // INCLUDE (covering) columns (PG11+). They are stored in the index without being part of
    // its key, which is what indnkeyatts vs indnatts below distinguishes.
    [Fact]
    public async Task IndexWithInclude_DeploysWithItsCoveringColumns()
    {
        const string indexSql =
            "CREATE INDEX ix_people_name ON people (name) INCLUDE (first_name, last_name);";

        // Prove the DDL is valid Postgres before asserting Squill reproduces it.
        await AssertExecutableAsync($"{PeopleTable}\n{indexSql}");

        await AssertIndexDeploysAndRoundTripsAsync(
            $"{PeopleTable}\n{indexSql}",
            "ix_people_name",
            async (db, ct) =>
            {
                // Two key columns plus one included column.
                await using var reader = await db.RunScriptReaderAsync("""
SELECT indnkeyatts, indnatts FROM pg_index WHERE indexrelid = 'ix_people_name'::regclass;
""", cancellationToken: ct);

                Assert.True(await reader.ReadAsync(ct));
                Assert.Equal(1, reader.GetInt16(0));
                Assert.Equal(3, reader.GetInt16(1));
            });
    }

    // An index over a bare function call — CREATE INDEX ix ON people (lower(name)) — takes
    // the func_expr_windowless alternative of index_elem.
    [Fact]
    public async Task ExpressionIndex_BareCall_DeploysAsAnExpressionIndex()
    {
        const string indexSql = "CREATE INDEX ix_people_lower_name ON people (lower(name));";

        await AssertExecutableAsync($"{PeopleTable}\n{indexSql}");

        await AssertIndexDeploysAndRoundTripsAsync(
            $"{PeopleTable}\n{indexSql}",
            "ix_people_lower_name",
            async (db, ct) =>
            {
                // The deployed index must be over the expression, not a plain column.
                await using var reader = await db.RunScriptReaderAsync("""
SELECT pg_get_indexdef('ix_people_lower_name'::regclass);
""", cancellationToken: ct);

                Assert.True(await reader.ReadAsync(ct));
                Assert.Contains("lower(name)", reader.GetString(0), StringComparison.Ordinal);
            });
    }

    // The parenthesized spelling of the same index — ((lower(name))) — takes the a_expr
    // alternative instead, so it reaches the model builder by a different route. Asserted
    // separately from the bare-call form above because they were two distinct defects: fixing
    // IndexElem alone would have left this one failing, and vice versa. Postgres stores one
    // canonical form for both, so both must reduce to the same model.
    [Fact]
    public async Task ExpressionIndex_Parenthesized_DeploysAsAnExpressionIndex()
    {
        const string indexSql = "CREATE INDEX ix_people_lower_name ON people ((lower(name)));";

        // This spelling always parsed; it was the model build that used to reject it.
        var root = new AntlrPostgresParser().Parse($"{PeopleTable}\n{indexSql}");
        Assert.Equal(2, root.Statements.Count);

        await AssertExecutableAsync($"{PeopleTable}\n{indexSql}");

        await AssertIndexDeploysAndRoundTripsAsync(
            $"{PeopleTable}\n{indexSql}",
            "ix_people_lower_name",
            async (db, ct) =>
            {
                await using var reader = await db.RunScriptReaderAsync("""
SELECT pg_get_indexdef('ix_people_lower_name'::regclass);
""", cancellationToken: ct);

                Assert.True(await reader.ReadAsync(ct));
                Assert.Contains("lower(name)", reader.GetString(0), StringComparison.Ordinal);
            });
    }

    // A bare operator class (name text_pattern_ops) is covered by PostgresVectorRoundTripTest;
    // this is the schema-qualified spelling. pg_catalog.text_pattern_ops is how a user would
    // disambiguate an opclass shadowed by one in another schema, and Postgres accepts it (the
    // built-in opclasses live in pg_catalog). The catalog reports the opclass unqualified, so
    // the qualifier must be dropped or the declaration would re-diff against its own database.
    [Fact]
    public async Task SchemaQualifiedOperatorClass_DeploysWithThatOperatorClass()
    {
        const string indexSql =
            "CREATE INDEX ix_people_name ON people USING btree (name pg_catalog.text_pattern_ops);";

        await AssertExecutableAsync($"{PeopleTable}\n{indexSql}");

        await AssertIndexDeploysAndRoundTripsAsync(
            $"{PeopleTable}\n{indexSql}",
            "ix_people_name",
            async (db, ct) =>
            {
                // The declared opclass must be the one the index actually uses.
                await using var reader = await db.RunScriptReaderAsync("""
SELECT o.opcname FROM pg_index i
JOIN pg_opclass o ON o.oid = i.indclass[0]
WHERE i.indexrelid = 'ix_people_name'::regclass;
""", cancellationToken: ct);

                Assert.True(await reader.ReadAsync(ct));
                Assert.Equal("text_pattern_ops", reader.GetString(0));
            });
    }

    // TABLESPACE pg_default. Measured: an index placed there stores reltablespace = 0 exactly
    // as one with no TABLESPACE clause does, and pg_get_indexdef omits the clause entirely — so
    // the default spelling is a genuine no-op, accepted and not modeled. A non-default
    // tablespace is a real placement decision and is rejected rather than dropped.
    [Fact]
    public async Task IndexTablespace_DeploysIntoThatTablespace()
    {
        const string indexSql = "CREATE INDEX ix_people_name ON people (name) TABLESPACE pg_default;";

        await AssertExecutableAsync($"{PeopleTable}\n{indexSql}");

        await AssertIndexDeploysAndRoundTripsAsync($"{PeopleTable}\n{indexSql}", "ix_people_name");
    }

    // The control case: changing an index's storage parameter between deploys. The CREATE
    // path is already covered, but nothing exercised a *change* to one, which must come out
    // as a drop-and-recreate carrying the new reloptions.
    [Fact]
    public async Task ChangeIndexStorageParameter_RecreatesIndex_AndPreservesData()
    {
        const string before = $"""
{PeopleTable}

CREATE INDEX ix_people_age ON people (age) WITH (fillfactor=70);
""";
        const string after = $"""
{PeopleTable}

CREATE INDEX ix_people_age ON people (age) WITH (fillfactor=80);
""";

        await RunDeployScenarioAsync(
            before, after,
            seedSql: "INSERT INTO people (id, name, age) VALUES (1, 'Ada', 36), (2, 'Alan', 41);",
            assertAfterAsync: async conn =>
            {
                // The rows must survive the index recreate.
                Assert.Equal(2L, await ScalarAsync(conn, "SELECT count(*) FROM people;"));

                // The deployed index must carry the new fillfactor, not the old one.
                var reloptions = (string[]?)await ScalarAsync(
                    conn, "SELECT reloptions FROM pg_class WHERE relname = 'ix_people_age';");
                Assert.NotNull(reloptions);
                Assert.Contains("fillfactor=80", reloptions);
            });
    }

    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            TestContext.Current.CancellationToken);

    // Builds the SQL, deploys it into a fresh database, and asserts the named index really was
    // created and that the source round-trips (re-extracting produces no deltas). The shared
    // shape of every index-variant scenario: the declared index must exist as declared, and it
    // must not re-diff on the next deploy.
    private async Task AssertIndexDeploysAndRoundTripsAsync(
        string sql, string indexName, Func<IDatabase, CancellationToken, Task>? assertShape = null)
    {
        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var model = await BuildModelAsync(sql);

        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var target = await dbModelBuilder.ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, model, target), ct);

            await using (var conn = await OpenAsync(testDb.Name, ct))
            {
                Assert.Equal(1L, await ScalarAsync(conn,
                    $"SELECT count(*) FROM pg_class WHERE relname = '{indexName}';"));
            }

            if (assertShape is not null)
            {
                await assertShape(testDb, ct);
            }

            // The variant must be modeled on both sides, or it re-diffs on every deploy.
            var deployed = await dbModelBuilder.ExtractModelAsync(ct);
            Assert.Empty(SchemaCompare.Compare(provider, model, deployed).Deltas);
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    // Runs `sql` against a throwaway database to prove the engine accepts the DDL Squill
    // refused — so the failing scenarios above are Squill gaps, not invalid SQL.
    private async Task AssertExecutableAsync(string sql)
    {
        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var db = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);

        try
        {
            await db.ConnectAsync(ct);
            await db.RunScriptAsync(sql, cancellationToken: ct);
        }
        finally
        {
            await db.DropAsync(ct);
        }
    }

    // Deploys `before`, seeds data, deploys `after` to the same database, then runs the
    // caller's assertions against the final database. Verifies the database's model matches
    // the `after` DACPAC's model along the way.
    private async Task RunDeployScenarioAsync(
        string before,
        string after,
        string seedSql,
        Func<NpgsqlConnection, Task> assertAfterAsync)
    {
        var ct = TestContext.Current.CancellationToken;
        var tempDir = Directory.CreateTempSubdirectory("squill-index-variant-integration");

        try
        {
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var targetDbName = $"squill_index_variant_{Guid.NewGuid():n}";
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
                    "An index change should generate a non-empty script.");

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

    // NULLS NOT DISTINCT (PG15+) was the silent-drop case in this file: unlike INCLUDE and
    // TABLESPACE above, nothing rejected it. The grammar accepted it, VisitIndexstmt never read
    // it and no property carried it, so the declared index deployed with the OPPOSITE uniqueness
    // semantics — multiple NULLs allowed where the source asked for them to collide — and the
    // loss was invisible to a round trip because neither side ever looked at it.
    //
    // Hence the second half of this test: asserting indnullsnotdistinct alone would pass on a
    // model that records the flag without the deploy honouring it, so the semantics are also
    // exercised directly against the data.
    [Fact]
    public async Task UniqueIndexNullsNotDistinct_IsDeployedWithTheDeclaredSemantics()
    {
        const string indexSql =
            "CREATE UNIQUE INDEX ix_people_age ON people (age) NULLS NOT DISTINCT;";
        var sql = $"{PeopleTable}\n{indexSql}";

        var ct = TestContext.Current.CancellationToken;
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var model = await BuildModelAsync(sql);
        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var target = await dbModelBuilder.ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, model, target), ct);

            await using var conn = await OpenAsync(testDb.Name, ct);

            // The catalog must record the declared semantics.
            Assert.Equal(true, await ScalarAsync(conn, """
SELECT indnullsnotdistinct FROM pg_index WHERE indexrelid = 'ix_people_age'::regclass;
"""));

            // And they must actually be enforced: with NULLS NOT DISTINCT a second NULL age
            // collides with the first, so the insert has to be rejected.
            await ExecuteAsync(conn, "INSERT INTO people (id, age) VALUES (1, NULL);", ct);

            await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
                conn, "INSERT INTO people (id, age) VALUES (2, NULL);", ct));
        }
        finally
        {
            await testDb.DropAsync(ct);
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

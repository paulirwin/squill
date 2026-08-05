using MySqlConnector;
using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// Index-fidelity tests for the MariaDB provider (issue #137), run against a real MariaDB or
/// MySQL server. These are not round-trip tests in the usual sense: every scenario here is one
/// where a declared index can deploy as <em>something other than what the user wrote</em>, with
/// no error and — because the source model and the extracted model are blind in the same way —
/// often no diff either. A hash-matching round trip therefore proves nothing on its own, so
/// each test also queries <c>information_schema.STATISTICS</c> and asserts the shape the engine
/// actually stored.
///
/// <para>
/// Each test asserts the shape that <em>should</em> deploy; those blocked by a known defect
/// carry a <c>[Fact(Skip = ...)]</c> naming the issue, so they turn green on their own once it
/// is fixed. The prefix-length and expression-key scenarios were such tests until #161 was
/// fixed, and the identifier-length one until #163 — all now run unskipped.
/// </para>
///
/// <para>
/// The scenarios are drawn from the index coverage in the EF Core providers
/// (<c>Pomelo.EntityFrameworkCore.MySql</c>, MIT, Copyright (c) 2017 Pomelo Foundation): index
/// prefix lengths, descending keys, and full-text indexes. The SQL below is original; only the
/// choice of scenario is borrowed.
/// </para>
///
/// <para>
/// The scenarios live on this abstract base and run once per engine via the concrete
/// <c>MariaDb*</c> / <c>MySql*</c> subclasses at the bottom of this file, because several of
/// them behave differently on the two engines.
/// </para>
/// </summary>
public abstract class MariaDbIndexFidelityTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    private bool IsMySql => Fixture.EngineName == "MySQL";

    private Model ParseModel(string sql, CancellationToken cancellationToken)
        => WorkspaceModelBuilding.BuildModelAsync(
                sql,
                ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser(), Fixture.SchemaProviderOf()),
                cancellationToken)
            .GetAwaiter().GetResult();

    // One deployed index key as the engine actually stored it.
    // EXPRESSION is MySQL-only (functional index keys); MariaDB's STATISTICS has no such column,
    // so it is queried only on MySQL and is null everywhere else.
    protected sealed record IndexKey(
        string IndexName, int Sequence, string? ColumnName, long? SubPart, string? Collation,
        string IndexType, string? Expression = null);

    /// <summary>
    /// Parses the SQL, deploys it into a fresh database, and hands the caller both the
    /// re-extracted model and a live connection to the deployed database, so the real deployed
    /// index shape can be inspected. The round-trip hash assertion and the redeploy-no-op
    /// assertion run first, exactly as <see cref="RoundTripHarness"/> does them — the extra
    /// <c>information_schema</c> inspection is what these tests add on top, since a model that
    /// drops an index key on both the source and the extraction side hash-matches happily while
    /// having deployed the wrong index.
    /// </summary>
    private async Task DeployAndInspectAsync(
        string sql,
        Func<Model, IReadOnlyList<IndexKey>, Task> assert,
        CancellationToken cancellationToken,
        bool assertRoundTrip = true)
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var parsedModel = ParseModel(sql, cancellationToken);

        var databaseName = $"squill_test_{Guid.NewGuid():n}";
        var testDb = await provider.CreateDatabaseAsync(databaseName, cancellationToken);
        var modelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await modelBuilder.ExtractModelAsync(cancellationToken);
            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, parsedModel, empty), cancellationToken);

            var extracted = await modelBuilder.ExtractModelAsync(cancellationToken);

            if (assertRoundTrip)
            {
                Assert.True(
                    HashUtility.HashesEqual(parsedModel.Hash, extracted.Hash),
                    $"[{Fixture.EngineName}] Parsed and extracted model hashes do not match.\n"
                    + $"Parsed:    {ModelAssertions.Describe(parsedModel)}\n"
                    + $"Extracted: {ModelAssertions.Describe(extracted)}");

                Assert.Empty(SchemaCompare.Compare(provider, parsedModel, extracted).Deltas);
            }

            await assert(extracted, await QueryIndexKeysAsync(databaseName, cancellationToken));
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    // Every index key of every table in the deployed database, as the engine stored it. This
    // query — SUB_PART in particular — is the whole point of this file: it reads the deployed
    // shape straight from the catalog, so a prefix length that never reached the DDL shows up
    // here even when the source and extracted models agree with each other (issue #161).
    private async Task<IReadOnlyList<IndexKey>> QueryIndexKeysAsync(
        string databaseName, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(
            new MySqlConnectionStringBuilder(Fixture.ConnectionString) { Database = databaseName }
                .ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // EXPRESSION exists only on MySQL; selecting it on MariaDB is an unknown-column error.
        var expressionColumn = IsMySql ? ", EXPRESSION" : string.Empty;

        await using var command = new MySqlCommand(
            $"""
            SELECT INDEX_NAME, SEQ_IN_INDEX, COLUMN_NAME, SUB_PART, COLLATION, INDEX_TYPE{expressionColumn}
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = @db
            ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX;
            """,
            connection);
        command.Parameters.AddWithValue("db", databaseName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var keys = new List<IndexKey>();
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(new IndexKey(
                reader.GetString("INDEX_NAME"),
                reader.GetInt32("SEQ_IN_INDEX"),
                reader.IsDBNull(reader.GetOrdinal("COLUMN_NAME")) ? null : reader.GetString("COLUMN_NAME"),
                reader.IsDBNull(reader.GetOrdinal("SUB_PART")) ? null : reader.GetInt64("SUB_PART"),
                reader.IsDBNull(reader.GetOrdinal("COLLATION")) ? null : reader.GetString("COLLATION"),
                reader.GetString("INDEX_TYPE"),
                IsMySql && !reader.IsDBNull(reader.GetOrdinal("EXPRESSION"))
                    ? reader.GetString("EXPRESSION")
                    : null));
        }

        return keys;
    }

    private static IReadOnlyList<IndexKey> KeysOf(IReadOnlyList<IndexKey> keys, string indexName)
        => keys.Where(k => k.IndexName == indexName).OrderBy(k => k.Sequence).ToList();

    private async Task<string> ServerVersionAsync(CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(Fixture.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand("SELECT VERSION();", connection);

        return (string)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    // ---- 1. Prefix lengths ----

    /// <summary>
    /// A prefix-length index key (<c>Brand(20)</c>) deploys with the declared prefix.
    ///
    /// <para>
    /// It used to deploy as a full-column key: the mapper read only the uid and ASC/DESC from
    /// each <c>indexColumnName</c>, and the extractor's STATISTICS query did not select
    /// <c>SUB_PART</c> — so the length was invisible from both ends and the round trip
    /// hash-matched while having deployed a different index (issue #161). Both sides read it
    /// now, which is why this asserts the catalog rather than trusting the hash.
    /// </para>
    ///
    /// <para>
    /// This is not an exotic corner: a prefix length is <em>mandatory</em> for indexing a TEXT
    /// or BLOB column on MySQL, and is the standard way to keep a composite key inside InnoDB's
    /// 3072-byte limit, so the silent drop blocks real schemas outright (see
    /// <see cref="PrefixLengthOnTextColumn_DeploysWithTheDeclaredPrefix"/>).
    /// </para>
    /// </summary>
    [Fact]
    public async Task IndexWithPrefixLength_DeploysWithTheDeclaredPrefix()
    {
        await DeployAndInspectAsync(
            """
            CREATE TABLE IceCreams
            (
                IceCreamId int NOT NULL,
                Brand      varchar(128) NOT NULL,
                Name       varchar(128) NOT NULL,
                PRIMARY KEY (IceCreamId)
            );
            CREATE INDEX IX_IceCreams_Brand ON IceCreams (Name, Brand(20));
            """,
            (_, keys) =>
            {
                var declared = KeysOf(keys, "IX_IceCreams_Brand");

                Assert.Equal(["Name", "Brand"], declared.Select(k => k.ColumnName));

                // The source declared Name in full and a 20-byte prefix of Brand.
                Assert.Null(declared[0].SubPart);
                Assert.Equal(20, declared[1].SubPart);

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The same prefix inside a PRIMARY KEY. <c>MapTableConstraint</c> routes a
    /// <c>PRIMARY KEY (Name, Brand(20))</c> through the same <c>MapIndexColumnNames</c>, so the
    /// same drop used to widen the deployed key to the whole column (issue #161). A PK is the
    /// one index where widening a key changes the table's uniqueness semantics, so this was
    /// more than an efficiency loss.
    /// </summary>
    [Fact]
    public async Task PrimaryKeyWithPrefixLength_DeploysWithTheDeclaredPrefix()
    {
        await DeployAndInspectAsync(
            """
            CREATE TABLE IceCreams
            (
                Brand varchar(64) NOT NULL,
                Name  varchar(64) NOT NULL,
                PRIMARY KEY (Name, Brand(20))
            );
            """,
            (_, keys) =>
            {
                var primary = KeysOf(keys, "PRIMARY");

                Assert.Equal(["Name", "Brand"], primary.Select(k => k.ColumnName));

                // The source declared Name in full and a 20-byte prefix of Brand. Widening the
                // key would change which rows the table accepts as unique.
                Assert.Null(primary[0].SubPart);
                Assert.Equal(20, primary[1].SubPart);

                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The scenario where the dropped prefix stopped being an efficiency question. With the
    /// prefix discarded the generated DDL indexed an unbounded TEXT column — and the two
    /// engines disagreed about that, in opposite and equally bad ways:
    ///
    /// <list type="bullet">
    /// <item>
    /// <b>MySQL</b> rejects it outright: <c>BLOB/TEXT column 'Body' used in key specification
    /// without a key length</c> (error 1170). Perfectly valid declarative SQL fails to deploy.
    /// </item>
    /// <item>
    /// <b>MariaDB</b> accepts it and silently substitutes its own prefix — the deployed index
    /// is <c>KEY ix_articles_body (Body(768))</c> where the source asked for 100. Squill cannot
    /// see either number, so it deploys the wrong index and reports success.
    /// </item>
    /// </list>
    ///
    /// Once the declared prefix is honoured the divergence disappears: <c>Body(100)</c> is legal
    /// on both engines and deploys identically, which is why this test asserts one outcome
    /// rather than branching per engine.
    /// </summary>
    [Fact]
    public async Task PrefixLengthOnTextColumn_DeploysWithTheDeclaredPrefix()
    {
        var ct = TestContext.Current.CancellationToken;

        const string sql = """
            CREATE TABLE articles
            (
                article_id int NOT NULL PRIMARY KEY,
                Body       text NOT NULL
            );
            CREATE INDEX ix_articles_body ON articles (Body(100));
            """;

        // A prefix length is mandatory for indexing TEXT on MySQL, so honouring it is what makes
        // this deployable at all — on both engines, identically.
        await DeployAndInspectAsync(
            sql,
            (_, keys) =>
            {
                var key = Assert.Single(KeysOf(keys, "ix_articles_body"));

                Assert.Equal("Body", key.ColumnName);
                Assert.Equal(100, key.SubPart);

                return Task.CompletedTask;
            },
            ct);
    }

    // ---- 2. Descending keys ----

    /// <summary>
    /// A descending index key. This one is the good news of the batch: the parser reads DESC,
    /// the generator emits it, the extractor reads <c>information_schema.STATISTICS.COLLATION</c>
    /// ('A'/'D') — but no integration test ever declared one, so nothing proved the three agreed
    /// end to end.
    ///
    /// <para>
    /// The engine caveat worth recording: MySQL has stored descending keys since 8.0, and
    /// MariaDB only since 10.8 — below that, MariaDB parses <c>DESC</c> and silently stores the
    /// key ascending, which would make this source a permanent phantom diff (every deploy would
    /// see a difference and recreate the index, forever). The version is read from the server
    /// rather than assumed, and the assertion is relaxed to "ascending, so the model would
    /// re-diff" only on a container old enough to have that behaviour.
    /// </para>
    /// </summary>
    [Fact]
    public async Task DescendingIndexColumn_RoundTripsAndIsStoredDescending()
    {
        var ct = TestContext.Current.CancellationToken;
        var version = await ServerVersionAsync(ct);
        var storesDescending = StoresDescendingKeys(version);

        await DeployAndInspectAsync(
            """
            CREATE TABLE readings
            (
                reading_id int NOT NULL PRIMARY KEY,
                sensor_id  int NOT NULL,
                taken_at   datetime NOT NULL
            );
            CREATE INDEX ix_readings_sensor ON readings (sensor_id, taken_at DESC);
            """,
            (_, keys) =>
            {
                var declared = KeysOf(keys, "ix_readings_sensor");

                Assert.Equal(["sensor_id", "taken_at"], declared.Select(k => k.ColumnName));
                Assert.Equal("A", declared[0].Collation);

                Assert.Equal(
                    storesDescending ? "D" : "A",
                    declared[1].Collation);

                return Task.CompletedTask;
            },
            ct,
            // On a server that does not store descending keys the extracted model reports the
            // key as ascending while the source says descending, so the round trip cannot
            // hash-match — that mismatch IS the phantom diff, and asserting it is the point.
            assertRoundTrip: storesDescending);
    }

    // Whether the server stores a descending index key as descending rather than silently
    // ascending: MySQL from 8.0, MariaDB from 10.8. Read from SELECT VERSION() rather than
    // assumed, since the test containers track :latest.
    private bool StoresDescendingKeys(string version)
    {
        var numeric = new string(version.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        var parts = numeric.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var major = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
        var minor = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;

        return IsMySql
            ? major > 8 || (major == 8 && minor >= 0)
            : major > 10 || (major == 10 && minor >= 8);
    }

    // ---- 3. Expression keys ----

    /// <summary>
    /// A functional (expression) index key deploys as declared.
    ///
    /// <para>
    /// It used to be discarded silently: <c>MapIndexColumnNames</c> skipped any
    /// <c>indexColumnName</c> that was not a plain uid, so
    /// <c>CREATE INDEX ix ON t ((a + b), c)</c> deployed as a ONE-column index on <c>c</c> with
    /// no warning — strictly worse than throwing, since a build error would at least have
    /// stopped a deploy that silently produces the wrong index (issue #161).
    /// </para>
    ///
    /// <para>
    /// The wrong-result branch is MySQL-only: MariaDB has no functional indexes and rejects
    /// the syntax at the server (<c>ERROR 1064</c>), so on MariaDB the source is not valid SQL
    /// in the first place. That is asserted directly against the server rather than skipped, so
    /// the divergence is on the record — Squill's parser accepts on both engines what only one
    /// of them can run.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ExpressionIndexKey_IsDeployedAsDeclared()
    {
        var ct = TestContext.Current.CancellationToken;

        const string sql = """
            CREATE TABLE totals
            (
                total_id int NOT NULL PRIMARY KEY,
                a        int NOT NULL,
                b        int NOT NULL,
                c        int NOT NULL
            );
            CREATE INDEX ix_totals_sum ON totals ((a + b), c);
            """;

        // The source declares a two-key index, so the model must carry both keys.
        var parsedModel = ParseModel(sql, ct);
        var sourceIndex = Assert.Single(
            parsedModel.Elements, e => e.Type == MariaDbElementTypes.SqlIndex);
        Assert.Equal(
            2,
            (sourceIndex.GetRelationship(MariaDbRelationshipNames.ColumnSpecifications)?.Entries
             ?? []).Count);

        if (!IsMySql)
        {
            // MariaDB has no functional indexes: the DDL the user wrote is not accepted by the
            // server at all, so there is no correct deploy for Squill to produce here. Assert
            // the engine's rejection so the engine difference is pinned rather than assumed.
            await Assert.ThrowsAsync<MySqlException>(() => ExecuteInFreshDatabaseAsync(sql, ct));
            return;
        }

        await DeployAndInspectAsync(
            sql,
            (_, keys) =>
            {
                var deployed = KeysOf(keys, "ix_totals_sum");

                // Both declared keys must be deployed: the expression key first, then column c.
                Assert.Equal(2, deployed.Count);
                Assert.NotNull(deployed[0].Expression);
                Assert.Equal("c", deployed[1].ColumnName);

                return Task.CompletedTask;
            },
            ct);
    }

    // Runs the raw SQL statements directly against a throwaway database, to confirm what the
    // engine itself makes of the source Squill was given.
    private async Task ExecuteInFreshDatabaseAsync(string sql, CancellationToken cancellationToken)
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var databaseName = $"squill_test_{Guid.NewGuid():n}";
        var testDb = await provider.CreateDatabaseAsync(databaseName, cancellationToken);

        try
        {
            foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries))
            {
                await ExecuteAsync(databaseName, statement, cancellationToken);
            }
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    // ---- 4. FULLTEXT ----

    /// <summary>
    /// An inline FULLTEXT index is modeled on both sides and survives a redeploy with drops
    /// enabled (issue #146).
    ///
    /// <para>
    /// It previously was not: the mapper returned <c>IgnoredTableConstraint</c> for a
    /// FULLTEXT/SPATIAL inline index, so the source model had none, while
    /// <c>MariaDbDatabaseModelBuilder.ExtractIndexesAsync</c> read it back from
    /// <c>information_schema.STATISTICS</c> as a <c>SqlIndex</c>. After the first deploy the
    /// target held an index the source model did not, so with <c>DropObjectsNotInSource</c>
    /// enabled the next deploy scripted a DROP of an index the user had explicitly declared.
    /// Sakila's <c>film_text</c> uses <c>FULLTEXT KEY</c>, so this is on the sample-project
    /// path.
    /// </para>
    /// </summary>
    [Fact]
    public async Task FulltextIndex_IsModeledAndDeployedAndNotDropped()
    {
        var ct = TestContext.Current.CancellationToken;

        const string sql = """
            CREATE TABLE film_text
            (
                film_id int NOT NULL PRIMARY KEY,
                title   varchar(255) NOT NULL,
                FULLTEXT KEY idx_title (title)
            );
            """;

        // The declared index must be in the source model (no database needed for this half).
        var parsedModel = ParseModel(sql, ct);

        var sourceIndex = Assert.Single(
            parsedModel.Elements, e => e.Type == MariaDbElementTypes.SqlIndex);
        Assert.Equal("idx_title", sourceIndex.Name?.ToString());

        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var databaseName = $"squill_test_{Guid.NewGuid():n}";
        var testDb = await provider.CreateDatabaseAsync(databaseName, ct);
        var modelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await modelBuilder.ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, parsedModel, empty), ct);

            // The deploy must create the declared fulltext index.
            var deployedKeys = await QueryIndexKeysAsync(databaseName, ct);
            Assert.Contains(deployedKeys, k => k.IndexType == "FULLTEXT" && k.IndexName == "idx_title");

            // And the two sides must agree, so a redeploy with drops enabled leaves it alone
            // rather than scripting a DROP of the user's own declared index.
            var extracted = await modelBuilder.ExtractModelAsync(ct);

            var withDrops = SchemaCompare.Compare(
                provider, parsedModel, extracted,
                new DeployOptions { DropObjectsNotInSource = true });

            Assert.Empty(withDrops.Deltas.OfType<DropDelta>());
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    /// <summary>
    /// The SPATIAL half of issue #146, which goes through the same code path as FULLTEXT but
    /// differs in two measured ways: the catalog reports its column ascending
    /// (<c>COLLATION = 'A'</c>, where a FULLTEXT column reports NULL), and the indexed column
    /// must be <c>NOT NULL</c> for either engine to accept the index at all.
    /// </summary>
    [Fact]
    public async Task SpatialIndex_IsModeledAndDeployedAndNotDropped()
    {
        var ct = TestContext.Current.CancellationToken;

        const string sql = """
            CREATE TABLE geo
            (
                geo_id int NOT NULL PRIMARY KEY,
                location geometry NOT NULL,
                SPATIAL KEY idx_location (location)
            );
            """;

        var parsedModel = ParseModel(sql, ct);

        var sourceIndex = Assert.Single(
            parsedModel.Elements, e => e.Type == MariaDbElementTypes.SqlIndex);
        Assert.Equal("idx_location", sourceIndex.Name?.ToString());

        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var databaseName = $"squill_test_{Guid.NewGuid():n}";
        var testDb = await provider.CreateDatabaseAsync(databaseName, ct);
        var modelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await modelBuilder.ExtractModelAsync(ct);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, parsedModel, empty), ct);

            var deployedKeys = await QueryIndexKeysAsync(databaseName, ct);
            Assert.Contains(deployedKeys, k => k.IndexType == "SPATIAL" && k.IndexName == "idx_location");

            var extracted = await modelBuilder.ExtractModelAsync(ct);

            var withDrops = SchemaCompare.Compare(
                provider, parsedModel, extracted,
                new DeployOptions { DropObjectsNotInSource = true });

            Assert.Empty(withDrops.Deltas.OfType<DropDelta>());
        }
        finally
        {
            await testDb.DropAsync(ct);
        }
    }

    /// <summary>
    /// A FULLTEXT index declared with the standalone <c>CREATE FULLTEXT INDEX</c> spelling must
    /// model identically to the inline <c>FULLTEXT KEY</c> form — measured against both engines,
    /// the two produce the same catalog rows, so they must produce the same model or one of the
    /// two would re-diff on every deploy.
    /// </summary>
    [Fact]
    public async Task StandaloneFulltextIndex_ModelsSameAsInlineForm()
    {
        var ct = TestContext.Current.CancellationToken;

        var inline = ParseModel("""
            CREATE TABLE film_text
            (
                film_id int NOT NULL PRIMARY KEY,
                title   varchar(255) NOT NULL,
                FULLTEXT KEY idx_title (title)
            );
            """, ct);

        var standalone = ParseModel("""
            CREATE TABLE film_text
            (
                film_id int NOT NULL PRIMARY KEY,
                title   varchar(255) NOT NULL
            );
            CREATE FULLTEXT INDEX idx_title ON film_text (title);
            """, ct);

        var inlineIndex = Assert.Single(inline.Elements, e => e.Type == MariaDbElementTypes.SqlIndex);
        var standaloneIndex = Assert.Single(standalone.Elements, e => e.Type == MariaDbElementTypes.SqlIndex);

        Assert.True(HashUtility.HashesEqual(inlineIndex.Hash, standaloneIndex.Hash));
    }

    // ---- 5. Identifier length ----

    /// <summary>
    /// An index name longer than the engines' 64-character identifier limit is rejected by the
    /// build with a source-anchored <c>SQ0005</c> diagnostic (issue #163).
    ///
    /// <para>
    /// It previously passed the build with no diagnostic — neither <c>SqlName</c> nor
    /// <c>MariaDbModelFactory</c> validated length — and failed at deploy time with
    /// <c>ERROR 1059 (42000): Identifier name '…' is too long</c>, after some of the script had
    /// already run. Per the repo's build-diagnostics policy (source-anchored SQ-class errors,
    /// the way SSDT rejects unresolved references at build time) that belongs at build time.
    /// The second half of this test still deploys the same SQL, so the build error stays
    /// justified by the engine's own rejection rather than Squill being gratuitously stricter
    /// than the target.
    /// </para>
    /// </summary>
    [Fact]
    public async Task LongIndexName_IsRejectedByTheBuild()
    {
        var ct = TestContext.Current.CancellationToken;

        // 70 characters: over the 64-character limit both engines enforce.
        var longName = "ix_" + new string('a', 67);
        Assert.Equal(70, longName.Length);

        var sql = $"""
            CREATE TABLE readings
            (
                reading_id int NOT NULL PRIMARY KEY,
                sensor_id  int NOT NULL
            );
            CREATE INDEX {longName} ON readings (sensor_id);
            """;

        // The build must reject it with a source-anchored diagnostic, the way an unresolved
        // reference or a duplicate table is rejected — not let it through to the server.
        var ex = Assert.Throws<SqlSourceException>(() => ParseModel(sql, ct));

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains("too long", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(6, ex.Line);

        // And the engine really would have rejected it, so the build error is warranted rather
        // than Squill being stricter than the target. 1059 = ER_TOO_LONG_IDENT.
        //
        // The raw statements go straight to the server rather than through the deploy path:
        // now that the build rejects this source, a deploy never reaches the server, so the
        // engine's own verdict has to be obtained directly for it to mean anything.
        var exception = await Assert.ThrowsAsync<MySqlException>(
            () => ExecuteInFreshDatabaseAsync(sql, ct));

        Assert.Equal(1059, exception.Number);
    }

    /// <summary>
    /// An expression key in a PRIMARY KEY is rejected by the build with a source-anchored
    /// <c>SQ0004</c> diagnostic (issue #209). It previously crashed the build with an unhandled
    /// <c>NullReferenceException</c>, because the primary-key path dereferenced the column of a
    /// key that names none.
    ///
    /// <para>
    /// Neither engine accepts the DDL, so there is no correct deploy to produce: MySQL rejects
    /// it with <c>ERROR 3756</c> ("The primary key cannot be a functional index") and MariaDB,
    /// which has no functional indexes at all, with a syntax error (<c>ERROR 1064</c>). Both
    /// verdicts are asserted against the server so the build error stays justified by the
    /// target rather than by Squill being stricter than it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ExpressionPrimaryKey_IsRejectedByTheBuild()
    {
        var ct = TestContext.Current.CancellationToken;

        const string sql = """
            CREATE TABLE totals
            (
                a int NOT NULL,
                PRIMARY KEY ((a + 1))
            );
            """;

        var ex = Assert.Throws<SqlSourceException>(() => ParseModel(sql, ct));

        Assert.Equal(SqlSourceException.InvalidConstraint, ex.Code);
        Assert.Contains("expression", ex.Message, StringComparison.OrdinalIgnoreCase);

        // 3756 = ER_FUNCTIONAL_INDEX_PRIMARY_KEY on MySQL; MariaDB cannot even parse the key,
        // so it answers 1064 = ER_PARSE_ERROR.
        var exception = await Assert.ThrowsAsync<MySqlException>(
            () => ExecuteInFreshDatabaseAsync(sql, ct));

        Assert.Equal(IsMySql ? 3756 : 1064, exception.Number);
    }

    private async Task ExecuteAsync(string databaseName, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new MySqlConnection(
            new MySqlConnectionStringBuilder(Fixture.ConnectionString) { Database = databaseName }
                .ConnectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class MariaDbIndexFidelityTestsMariaDb(MariaDbFixture fixture)
    : MariaDbIndexFidelityTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbIndexFidelityTestsMySql(MySqlFixture fixture)
    : MariaDbIndexFidelityTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

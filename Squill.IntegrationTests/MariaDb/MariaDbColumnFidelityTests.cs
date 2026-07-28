using MySqlConnector;
using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// End-to-end coverage against real MariaDB and MySQL for issue #137: the column- and
/// table-level facets a source file may legally declare that the MariaDB mapper recognizes
/// but does not model, plus the two engine divergences that can turn a clean build into a
/// deploy that either fails outright or deploys something other than what was written.
///
/// Three distinct hazards are separated here, because they warrant different responses:
/// <list type="number">
///   <item>a construct the build accepts that the *engine* then rejects (a TEXT column's
///     literal <c>DEFAULT</c>, which MySQL refuses and MariaDB allows);</item>
///   <item>a construct that silently vanishes from the deployed schema — a column
///     <c>COLLATE</c> or <c>COMMENT</c> warns SQ1002, but a table-level <c>COLLATE=</c> /
///     <c>COMMENT=</c> is dropped without so much as a warning, because
///     <c>MariaDbStatementMapper.MapCreateTable</c> never visits the table-option list, and a
///     <c>json</c> column is rewritten to <c>longtext</c> before it reaches the server;</item>
///   <item>a facet each engine stores or reports differently, where the risk is a phantom
///     diff on every deploy rather than a failure (a referential action's reported rule, a
///     string default containing a quote or a backslash).</item>
/// </list>
///
/// Every round trip asserts <c>assertRedeployNoOp: true</c>. For the discarded facets that is
/// the whole point: both the parser and the extractor are blind to them in the same way, so
/// the redeploy is clean and nothing ever surfaces the loss — which is exactly why these gaps
/// went unnoticed. The tests below pin what is actually deployed by querying
/// <c>information_schema</c> directly, not by reading Squill's own model back.
///
/// Two scenarios here are defects rather than documented trade-offs, so unlike the rest of the
/// file they assert the CORRECT behaviour and carry a <c>[Fact(Skip = ...)]</c> naming issue
/// #162: <c>NVARCHAR(45)</c> loses its length on the way to the DDL and deploys as a syntax
/// error, and <c>REAL</c> is never folded to the <c>double</c> both engines store it as, so a
/// column declared REAL re-diffs on every deploy. They go green on their own once #162 is
/// fixed, rather than freezing the bug in place.
///
/// The scenarios live on this abstract base and run once per engine via the concrete
/// <c>MariaDb*</c> / <c>MySql*</c> subclasses at the bottom of this file.
/// </summary>
public abstract class MariaDbColumnFidelityTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    private bool IsMySql => Fixture.EngineName == "MySQL";

    private async Task<BuildResult> BuildAsync(string sql, CancellationToken cancellationToken)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return await new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser(), (MariaDbFamilyDatabaseSchemaProvider)Fixture.SchemaProvider)
            .ExtractModelAsync(cancellationToken);
    }

    private async Task<Model> ParseAsync(string sql, CancellationToken cancellationToken)
        => (await BuildAsync(sql, cancellationToken)).Model;

    // The shared round trip: parse, publish into a fresh database, re-extract, assert the two
    // models hash-match, and assert redeploying the same source is a no-op.
    private async Task<Model> AssertRoundTripAsync(string sql, CancellationToken cancellationToken)
        => await RoundTripHarness.AssertRoundTripAsync(
            new MariaDbDatabaseProvider(Fixture.ConnectionString),
            await ParseAsync(sql, cancellationToken),
            Fixture.EngineName,
            assertRedeployNoOp: true,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Publishes <paramref name="sql"/> into a fresh database and hands the still-live database
    /// to <paramref name="inspect"/>, so a test can ask the server what it actually got rather
    /// than asking Squill what it thinks it deployed. The round-trip harness drops its database
    /// before returning, which is why this exists alongside it.
    /// </summary>
    private async Task DeployAndInspectAsync(
        string sql,
        Func<IDatabase, string, Task> inspect,
        CancellationToken cancellationToken)
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var parsed = await ParseAsync(sql, cancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);

        try
        {
            var builder = provider.CreateDatabaseModelBuilder(testDb);
            var empty = await builder.ExtractModelAsync(cancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, parsed, empty), cancellationToken);

            await inspect(testDb, testDb.Name);
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    private static async Task<string?> ScalarAsync(
        IDatabase database, string sql, CancellationToken cancellationToken)
    {
        await using var reader = await database.RunScriptReaderAsync(
            sql, cancellationToken: cancellationToken);

        return await reader.ReadAsync(cancellationToken) && !reader.IsDBNull(0)
            ? reader.GetString(0)
            : null;
    }

    // ---------------------------------------------------------------------------------------
    // 1. A literal DEFAULT on an unlimited-text column: accepted by MariaDB, rejected by MySQL.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A literal <c>DEFAULT</c> on a <c>longtext</c> column is a build-succeeds /
    /// deploy-may-fail hazard. <c>MariaDbDefaultValue.FromSourceToken</c> canonicalizes the
    /// quoted literal like any other string default and the script generator emits it, but the
    /// engines disagree about whether that is legal DDL at all: MariaDB accepts it, MySQL
    /// rejects it with
    /// <c>ERROR 1101: BLOB, TEXT, GEOMETRY or JSON column 'Event' can't have a default value</c>
    /// and requires the parenthesized expression form instead.
    ///
    /// The build cannot tell the difference — one provider serves both engines and the target
    /// is not known until deploy — so this test pins the divergence rather than pretending it
    /// away: the same DACPAC deploys on MariaDB and fails on MySQL.
    /// </summary>
    [Fact]
    public async Task DefaultOnUnlimitedTextColumn_DeploysOnMariaDbAndIsRejectedByMySql()
    {
        const string sql = "CREATE TABLE History (Event longtext NOT NULL DEFAULT 'The Battle of Waterloo');";

        var cancellationToken = TestContext.Current.CancellationToken;

        // The build is happy on both engines: the default is a modeled constant literal.
        var build = await BuildAsync(sql, cancellationToken);
        Assert.Empty(build.Warnings);

        if (IsMySql)
        {
            var ex = await Assert.ThrowsAsync<MySqlException>(
                () => AssertRoundTripAsync(sql, cancellationToken));

            Assert.Contains("can't have a default value", ex.Message);

            return;
        }

        // MariaDB takes it, and reports it back quoted, so the round trip is clean.
        await AssertRoundTripAsync(sql, cancellationToken);

        await DeployAndInspectAsync(sql, async (db, name) =>
        {
            Assert.Equal("'The Battle of Waterloo'", await ScalarAsync(db, $"""
                SELECT COLUMN_DEFAULT FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = '{name}' AND TABLE_NAME = 'History' AND COLUMN_NAME = 'Event';
                """, cancellationToken));
        }, cancellationToken);
    }

    /// <summary>
    /// The portable spelling of the same intent — <c>DEFAULT ('literal')</c> — which both
    /// engines accept on a TEXT column. Squill does not model it:
    /// <c>MariaDbDefaultValue.IsExpressionDefault</c> rejects anything containing a
    /// parenthesis, so the default is dropped with an SQ1002 warning rather than risking a
    /// round trip it cannot make. That is the safe outcome, and the point of this test is that
    /// the *round trip stays clean* — an unmodeled default must not become a perpetual diff,
    /// the same proof <c>LocalTimeDefault_IsNotFoldedAndStillRoundTrips</c> provides.
    ///
    /// The two engines' catalogs are why folding it in would be unsafe: MariaDB collapses the
    /// parentheses and reports <c>'The Battle of Waterloo'</c>, while MySQL reports the
    /// expression as <c>_latin1\'The Battle of Waterloo\'</c> with <c>DEFAULT_GENERATED</c> in
    /// EXTRA. No single canonical token matches both.
    /// </summary>
    [Fact]
    public async Task ParenthesizedDefaultOnTextColumn_IsUnmodeledAndStillRoundTrips()
    {
        const string sql = "CREATE TABLE History (Event longtext NOT NULL DEFAULT ('The Battle of Waterloo'));";

        var cancellationToken = TestContext.Current.CancellationToken;

        var build = await BuildAsync(sql, cancellationToken);

        var warning = Assert.Single(build.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("History.Event", warning.Message);

        var model = await AssertRoundTripAsync(sql, cancellationToken);

        Assert.Null(Column(model, "Event").GetProperty<string>(MariaDbPropertyNames.DefaultValue));
    }

    // ---------------------------------------------------------------------------------------
    // 2. String defaults carrying characters that have to be escaped.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A string default containing an embedded single quote and a double quote.
    /// <c>MariaDbDefaultValue</c> unescapes the source literal and re-escapes it as a
    /// single-quoted one, and <c>FromDatabaseText</c> re-quotes the bare form MySQL reports for
    /// a character column — but until issue #137 nothing, unit or integration, had ever fed it
    /// a quote to escape.
    ///
    /// The two engines report this differently, which is exactly where a mis-escape would
    /// show: MariaDB reports <c>'it''s a "test"'</c> (quoted, doubled), MySQL reports the bare
    /// <c>it's a "test"</c>. Both must reach the same canonical token, and the value the server
    /// actually stores must be the one the source wrote.
    /// </summary>
    [Fact]
    public async Task StringDefaultWithEmbeddedQuotes_RoundTripsAndDeploysByteCorrect()
    {
        const string sql = """
            CREATE TABLE quoted_note
            (
                Note varchar(255) NOT NULL DEFAULT 'it''s a "test"'
            );
            """;

        var cancellationToken = TestContext.Current.CancellationToken;

        var model = await AssertRoundTripAsync(sql, cancellationToken);

        // One canonical token on both engines, single-quoted with the inner quote doubled.
        Assert.Equal("'it''s a \"test\"'", DefaultOf(model, "Note"));

        await DeployAndInspectAsync(sql, async (db, name) =>
        {
            // Not the catalog's spelling of the default but the value a row actually gets:
            // that is the only assertion an escaping bug cannot slip past, since the two
            // engines spell COLUMN_DEFAULT differently but must store the same string.
            await db.RunScriptAsync(
                "INSERT INTO quoted_note () VALUES ();", cancellationToken: cancellationToken);

            Assert.Equal(
                "it's a \"test\"",
                await ScalarAsync(db, "SELECT Note FROM quoted_note;", cancellationToken));
        }, cancellationToken);
    }

    /// <summary>
    /// The same, with a backslash — the other character both engines treat as an escape inside
    /// a string literal. A doubled backslash in source means one literal backslash in the
    /// stored value.
    /// </summary>
    [Fact]
    public async Task StringDefaultWithBackslash_DeploysByteCorrect()
    {
        const string sql = """
            CREATE TABLE pathy
            (
                Location varchar(255) NOT NULL DEFAULT 'C:\\logs'
            );
            """;

        var cancellationToken = TestContext.Current.CancellationToken;

        await DeployAndInspectAsync(sql, async (db, name) =>
        {
            await db.RunScriptAsync(
                "INSERT INTO pathy () VALUES ();", cancellationToken: cancellationToken);

            Assert.Equal(
                @"C:\logs",
                await ScalarAsync(db, "SELECT Location FROM pathy;", cancellationToken));
        }, cancellationToken);
    }

    // ---------------------------------------------------------------------------------------
    // 3./4. Collation, at the column and at the table level, is discarded.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A per-column <c>COLLATE</c> is mapped to <c>IgnoredColumnConstraint</c> and dropped. It
    /// at least warns (SQ1002), but the deployed column silently gets the table's collation
    /// instead of the declared one — and because the extractor's column query does not select
    /// <c>COLLATION_NAME</c> either, both sides of the diff are blind to it and the redeploy is
    /// a clean no-op. That symmetric blindness is precisely why the loss goes unnoticed.
    ///
    /// The user-visible consequence is asserted rather than described: the column is declared
    /// with a case-<i>sensitive</i> collation and made UNIQUE, so <c>'a'</c> and <c>'A'</c> are
    /// two distinct values and both inserts should succeed. What is actually deployed is the
    /// server's case-<i>insensitive</i> default, under which the second insert is a duplicate
    /// key — the schema silently enforces a constraint the source did not ask for.
    /// </summary>
    [Fact]
    public async Task ColumnLevelCollation_IsDiscarded()
    {
        const string sql = """
            CREATE TABLE Mountains
            (
                Name varchar(255) NOT NULL COLLATE latin1_general_cs,
                CONSTRAINT uq_mountains_name UNIQUE (Name)
            );
            """;

        var cancellationToken = TestContext.Current.CancellationToken;

        var build = await BuildAsync(sql, cancellationToken);

        var warning = Assert.Single(build.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("Mountains.Name", warning.Message);
        Assert.Contains("COLLATE", warning.Message);

        // Blind on both sides, so the round trip is clean despite the lost collation.
        await AssertRoundTripAsync(sql, cancellationToken);

        await DeployAndInspectAsync(sql, async (db, name) =>
        {
            var collation = await ScalarAsync(db, $"""
                SELECT COLLATION_NAME FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = '{name}' AND TABLE_NAME = 'Mountains' AND COLUMN_NAME = 'Name';
                """, cancellationToken);

            Assert.NotEqual("latin1_general_cs", collation);

            await db.RunScriptAsync(
                "INSERT INTO Mountains (Name) VALUES ('a');", cancellationToken: cancellationToken);

            // Under the declared latin1_general_cs this is a second, distinct value. Under the
            // case-insensitive collation actually deployed it is a duplicate, and the engine
            // rejects a row the source's schema would have accepted.
            var ex = await Assert.ThrowsAsync<MySqlException>(() => db.RunScriptAsync(
                "INSERT INTO Mountains (Name) VALUES ('A');", cancellationToken: cancellationToken));

            Assert.Contains("Duplicate entry", ex.Message);
        }, cancellationToken);
    }

    /// <summary>
    /// The table-option form, and the worse of the two: <c>MapCreateTable</c> iterates only
    /// <c>createDefinitions()</c> and never looks at the option list after the closing paren, so
    /// a table-level <c>COLLATE</c> is discarded with <b>no diagnostic at all</b>. The build is
    /// clean, the round trip is clean, and the deployed table has the server's default
    /// collation.
    ///
    /// This is not cosmetic. A table's default character set determines the storage type of
    /// every unqualified string column in it, so the same source deployed to two servers with
    /// different defaults produces columns of different types — and, on a server whose default
    /// is not what the source assumed, a <c>varchar(255)</c> may not even fit in an index.
    /// </summary>
    [Fact]
    public async Task TableLevelCollation_IsDiscardedWithoutEvenAWarning()
    {
        const string sql = """
            CREATE TABLE Mountains
            (
                Name varchar(255) NOT NULL
            ) COLLATE latin1_general_ci;
            """;

        var cancellationToken = TestContext.Current.CancellationToken;

        var build = await BuildAsync(sql, cancellationToken);

        // The silent case: nothing tells the user the collation will not be deployed.
        Assert.Empty(build.Warnings);

        await AssertRoundTripAsync(sql, cancellationToken);

        await DeployAndInspectAsync(sql, async (db, name) =>
        {
            var tableCollation = await ScalarAsync(db, $"""
                SELECT TABLE_COLLATION FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = '{name}' AND TABLE_NAME = 'Mountains';
                """, cancellationToken);

            Assert.NotEqual("latin1_general_ci", tableCollation);

            // What it got instead: the database default, which is a server-configuration
            // property rather than anything the source said.
            var schemaCollation = await ScalarAsync(db, $"""
                SELECT DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA
                WHERE SCHEMA_NAME = '{name}';
                """, cancellationToken);

            Assert.Equal(schemaCollation, tableCollation);
        }, cancellationToken);
    }

    // ---------------------------------------------------------------------------------------
    // 5. Comments, at the column and at the table level, are discarded.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Comments are documentation a user deliberately writes into the schema, and both levels
    /// are dropped — but only one of them says so. A column <c>COMMENT</c> reaches
    /// <c>IgnoredColumnConstraint</c> and warns SQ1002; a table-level <c>COMMENT=</c> is a table
    /// option, which <c>MapCreateTable</c> never visits, so it disappears in silence. The silent
    /// half is the more dangerous one, and this test pins the asymmetry so that closing the gap
    /// is a deliberate change rather than an accident.
    /// </summary>
    [Fact]
    public async Task ColumnAndTableComments_AreDiscarded()
    {
        const string sql = """
            CREATE TABLE People
            (
                Id   int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                Name longtext NULL COMMENT 'My comment'
            ) COMMENT='Table comment';
            """;

        var cancellationToken = TestContext.Current.CancellationToken;

        var build = await BuildAsync(sql, cancellationToken);

        // Exactly one warning: the column's. The table's produces none.
        var warning = Assert.Single(build.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("People.Name", warning.Message);
        Assert.Contains("COMMENT", warning.Message);

        await AssertRoundTripAsync(sql, cancellationToken);

        await DeployAndInspectAsync(sql, async (db, name) =>
        {
            Assert.Equal(string.Empty, await ScalarAsync(db, $"""
                SELECT COLUMN_COMMENT FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = '{name}' AND TABLE_NAME = 'People' AND COLUMN_NAME = 'Name';
                """, cancellationToken));

            Assert.Equal(string.Empty, await ScalarAsync(db, $"""
                SELECT TABLE_COMMENT FROM information_schema.TABLES
                WHERE TABLE_SCHEMA = '{name}' AND TABLE_NAME = 'People';
                """, cancellationToken));
        }, cancellationToken);
    }

    // ---------------------------------------------------------------------------------------
    // 6. json, the type it is silently rewritten to, and the CHECK MariaDB would synthesize.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A <c>json</c> column never reaches either server as <c>json</c>:
    /// <c>MariaDbTypeNormalizer.Canonicalize</c> folds it to <c>longtext</c> so one model can
    /// hash-match on both engines, and the script generator emits <c>longtext</c>. On MariaDB
    /// that is exactly what a JSON column is stored as, so nothing is lost there. On MySQL,
    /// which has a genuine native <c>json</c> type, the declared type is quietly downgraded —
    /// the deployed column accepts any text, loses the server's JSON validation, and cannot
    /// be indexed or queried as JSON.
    ///
    /// The round trip is clean either way, because the extractor reads back the same
    /// <c>longtext</c> the generator wrote, so no diff will ever report the downgrade. This
    /// test pins it: the deployed type is asserted directly from the catalog on both engines.
    ///
    /// The corollary matters for the drop path. MariaDB attaches an unnamed
    /// <c>CHECK (json_valid(`col`))</c> to a column declared <c>JSON</c>, and Squill extracts
    /// CHECK constraints as droppable standalone elements — so a redeploy with
    /// <c>DropObjectsNotInSource</c> enabled would see a constraint no source declares. Because
    /// the emitted DDL says <c>longtext</c>, no such constraint is ever created and the hazard
    /// does not arise here; the assertion below records that, so if the json fold is ever
    /// removed this test is where the drop hazard surfaces.
    /// </summary>
    [Fact]
    public async Task JsonColumn_IsDeployedAsLongtextAndSynthesizesNoCheckToDrop()
    {
        const string sql = """
            CREATE TABLE PlaceDetails
            (
                Id              int NOT NULL PRIMARY KEY,
                Characteristics json NULL
            );
            """;

        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var parsed = await ParseAsync(sql, cancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);

        try
        {
            var builder = provider.CreateDatabaseModelBuilder(testDb);

            await testDb.PublishAsync(
                SchemaCompare.Compare(
                    provider, parsed, await builder.ExtractModelAsync(cancellationToken)),
                cancellationToken);

            var extracted = await builder.ExtractModelAsync(cancellationToken);

            // On MySQL this would be `json` had the declared type survived; on MariaDB a real
            // JSON column reports longtext too, but would carry the json_valid CHECK below.
            Assert.Equal("longtext", await ScalarAsync(testDb, $"""
                SELECT COLUMN_TYPE FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = '{testDb.Name}' AND TABLE_NAME = 'PlaceDetails'
                  AND COLUMN_NAME = 'Characteristics';
                """, cancellationToken));

            // No engine-synthesized constraint exists, on either engine, because no column was
            // ever declared JSON.
            Assert.Equal("0", await ScalarAsync(testDb, $"""
                SELECT CAST(COUNT(*) AS CHAR) FROM information_schema.CHECK_CONSTRAINTS
                WHERE CONSTRAINT_SCHEMA = '{testDb.Name}' AND CHECK_CLAUSE LIKE '%json_valid%';
                """, cancellationToken));

            // Redeploying the same source with drops enabled is a no-op. The harness's ordinary
            // no-op assertion uses the default options, which never produce a DropDelta at all,
            // so drops have to be switched on explicitly for this to mean anything.
            var withDrops = SchemaCompare.Compare(
                provider, parsed, extracted, new DeployOptions { DropObjectsNotInSource = true });

            Assert.Empty(withDrops.Deltas);
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    // ---------------------------------------------------------------------------------------
    // 7. Foreign-key referential actions.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// All five referential actions the parser renders, only two of which any existing
    /// integration test exercised (CASCADE and RESTRICT). The trap is that neither engine
    /// reports back the word that was written, and the two do not agree with each other:
    /// <c>RESTRICT</c> and <c>NO ACTION</c> are the same behavior under both, but MariaDB calls
    /// it <c>RESTRICT</c> in <c>REFERENTIAL_CONSTRAINTS</c> while MySQL calls it
    /// <c>NO ACTION</c> — including for a rule that was never declared at all, which each
    /// engine fills in with its own spelling of the same default.
    ///
    /// <c>MariaDbDatabaseModelBuilder.MapReferentialAction</c> folds both words to one modeled
    /// action, and the generator emits that one word back. That fold is what keeps a declared
    /// <c>NO ACTION</c> from re-diffing on every deploy — exactly the class of bug
    /// <c>MariaDbDefaultValue</c> was written to prevent for column defaults —
    /// and <c>assertRedeployNoOp: true</c> is what proves it holds on both engines.
    ///
    /// The catalog assertion below is therefore written in terms of the *behavior* each rule
    /// selects, not the word the source used: <c>CASCADE</c> and <c>SET NULL</c> are reported
    /// verbatim, while <c>RESTRICT</c>, <c>NO ACTION</c> and an omitted rule all land on the
    /// engine's own word for restrict.
    /// </summary>
    [Theory]
    [InlineData("ON DELETE CASCADE", "CASCADE", null)]
    [InlineData("ON DELETE SET NULL", "SET NULL", null)]
    [InlineData("ON DELETE RESTRICT", null, null)]
    [InlineData("ON DELETE NO ACTION", null, null)]
    [InlineData("ON UPDATE CASCADE", null, "CASCADE")]
    public async Task ForeignKeyReferentialActions_RoundTripAndDeployWithTheDeclaredBehavior(
        string action, string? expectedDeleteRule, string? expectedUpdateRule)
    {
        var sql = $"""
            CREATE TABLE ref_parent
            (
                id int NOT NULL PRIMARY KEY
            );
            CREATE TABLE ref_child
            (
                id        int NOT NULL PRIMARY KEY,
                parent_id int NULL,
                CONSTRAINT fk_ref_child_parent FOREIGN KEY (parent_id)
                    REFERENCES ref_parent (id) {action}
            );
            """;

        var cancellationToken = TestContext.Current.CancellationToken;

        await AssertRoundTripAsync(sql, cancellationToken);

        await DeployAndInspectAsync(sql, async (db, name) =>
        {
            var deleteRule = await ScalarAsync(db, $"""
                SELECT DELETE_RULE FROM information_schema.REFERENTIAL_CONSTRAINTS
                WHERE CONSTRAINT_SCHEMA = '{name}' AND CONSTRAINT_NAME = 'fk_ref_child_parent';
                """, cancellationToken);

            var updateRule = await ScalarAsync(db, $"""
                SELECT UPDATE_RULE FROM information_schema.REFERENTIAL_CONSTRAINTS
                WHERE CONSTRAINT_SCHEMA = '{name}' AND CONSTRAINT_NAME = 'fk_ref_child_parent';
                """, cancellationToken);

            // The engine's own word for restrict, which is what every rule that is not CASCADE
            // or SET NULL — declared RESTRICT, declared NO ACTION, or omitted entirely — comes
            // back as. The two engines choose different words for the identical behavior.
            var restrict = IsMySql ? "NO ACTION" : "RESTRICT";

            Assert.Equal(expectedDeleteRule ?? restrict, deleteRule);
            Assert.Equal(expectedUpdateRule ?? restrict, updateRule);
        }, cancellationToken);
    }

    // ---------------------------------------------------------------------------------------
    // 8. Uppercase and alias type spellings.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Every data-type round trip so far was written in lowercase and used the canonical
    /// spelling, which is not how real source is written. The uppercase spellings must reach
    /// the same canonical type, and the alias types must resolve to what the engine actually
    /// stores rather than to the word that was typed: <c>INTEGER</c> stores as <c>int</c> and
    /// <c>DEC</c> as <c>decimal</c>, both of which <c>MariaDbTypeNormalizer.Canonicalize</c>
    /// folds so the parsed and extracted models agree.
    ///
    /// A varchar is included alongside them because it is the case where the declared length
    /// has to survive the alias canonicalization — a length dropped on the way to the DDL is
    /// not a fidelity nit but invalid SQL.
    ///
    /// Two more alias spellings belong in this list and are deliberately absent, because
    /// including them would only assert a defect: <c>NVARCHAR(45)</c> loses its length and
    /// deploys as a syntax error, and <c>REAL</c> is not folded to the <c>double</c> both
    /// engines store it as, so it re-diffs on every deploy. Both are reported against issue
    /// #137 rather than encoded here.
    /// </summary>
    [Fact]
    public async Task UppercaseAndAliasTypeNames_RoundTripAndResolveToTheEnginesTypes()
    {
        const string sql = """
            CREATE TABLE IceCream
            (
                Id   INT NOT NULL,
                Name VARCHAR(45) NULL,
                N    INTEGER NULL,
                D    DEC(5, 2) NULL
            );
            """;

        var cancellationToken = TestContext.Current.CancellationToken;

        await AssertRoundTripAsync(sql, cancellationToken);

        await DeployAndInspectAsync(sql, async (db, name) =>
        {
            async Task<(string DataType, long? MaxLength)> ColumnFacetsAsync(string column)
            {
                await using var reader = await db.RunScriptReaderAsync($"""
                    SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM information_schema.COLUMNS
                    WHERE TABLE_SCHEMA = '{name}' AND TABLE_NAME = 'IceCream'
                      AND COLUMN_NAME = '{column}';
                    """, cancellationToken: cancellationToken);

                Assert.True(await reader.ReadAsync(cancellationToken));

                return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt64(1));
            }

            Assert.Equal("int", (await ColumnFacetsAsync("Id")).DataType);
            Assert.Equal("int", (await ColumnFacetsAsync("N")).DataType);
            Assert.Equal("decimal", (await ColumnFacetsAsync("D")).DataType);

            var varchar = await ColumnFacetsAsync("Name");
            Assert.Equal("varchar", varchar.DataType);
            Assert.Equal(45, varchar.MaxLength);
        }, cancellationToken);
    }

    /// <summary>
    /// A declared <c>NVARCHAR(45)</c> must keep its length through to the generated DDL and
    /// deploy as a national varchar of that length.
    /// </summary>
    /// <remarks>
    /// <c>MariaDbScriptGenerator.GetTypeStringForColumn</c> preserves a declared length only for
    /// <c>varchar</c>, <c>char</c>, <c>binary</c> and <c>varbinary</c>. <c>nvarchar</c> is not in
    /// that set, so the generated DDL says a bare <c>nvarchar</c> and both engines reject it as a
    /// syntax error. The build itself succeeds with no warning, so this only surfaces at deploy.
    /// </remarks>
    [Fact(Skip = "Blocked by issue #162: GetTypeStringForColumn drops the length from nvarchar, "
                 + "so the generated DDL is a bare `nvarchar` and the deploy fails with a syntax error.")]
    public async Task NvarcharColumn_KeepsItsDeclaredLength()
    {
        const string sql = """
            CREATE TABLE IceCream
            (
                Id   int NOT NULL,
                Name NVARCHAR(45) NULL
            );
            """;

        var cancellationToken = TestContext.Current.CancellationToken;

        await AssertRoundTripAsync(sql, cancellationToken);

        await DeployAndInspectAsync(sql, async (db, name) =>
        {
            await using var reader = await db.RunScriptReaderAsync($"""
                SELECT DATA_TYPE, CHARACTER_MAXIMUM_LENGTH FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = '{name}' AND TABLE_NAME = 'IceCream'
                  AND COLUMN_NAME = 'Name';
                """, cancellationToken: cancellationToken);

            Assert.True(await reader.ReadAsync(cancellationToken));

            // Both engines store NVARCHAR as a varchar with the utf8 character set; the length
            // is the part that must survive.
            Assert.Equal("varchar", reader.GetString(0));
            Assert.Equal(45, reader.GetInt64(1));
        }, cancellationToken);
    }

    /// <summary>
    /// A column declared <c>REAL</c> must round-trip: both engines store it as <c>double</c>
    /// (REAL is a documented synonym unless <c>REAL_AS_FLOAT</c> is set, which it is not by
    /// default on either), so the type normalizer must fold the two together or every redeploy
    /// of unchanged source produces a phantom delta.
    /// </summary>
    /// <remarks>
    /// <c>MariaDbTypeNormalizer.Canonicalize</c> folds <c>integer</c> to <c>int</c> and similar,
    /// but has no <c>real</c> to <c>double</c> rule, so the parsed model says <c>real</c> while
    /// the extracted model says <c>double</c>.
    /// </remarks>
    [Fact(Skip = "Blocked by issue #162: MariaDbTypeNormalizer never folds real to double, so "
                 + "the parsed and extracted models disagree and the source re-diffs on every deploy.")]
    public async Task RealColumn_RoundTripsAsDouble()
    {
        const string sql = """
            CREATE TABLE IceCream
            (
                Id int NOT NULL,
                C  REAL NULL
            );
            """;

        var cancellationToken = TestContext.Current.CancellationToken;

        // The assertion that matters: this asserts a hash match and a no-op redeploy, which is
        // exactly what fails today (parsed=real, extracted=double, one delta every deploy).
        await AssertRoundTripAsync(sql, cancellationToken);

        await DeployAndInspectAsync(sql, async (db, name) =>
        {
            await using var reader = await db.RunScriptReaderAsync($"""
                SELECT DATA_TYPE FROM information_schema.COLUMNS
                WHERE TABLE_SCHEMA = '{name}' AND TABLE_NAME = 'IceCream'
                  AND COLUMN_NAME = 'C';
                """, cancellationToken: cancellationToken);

            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal("double", reader.GetString(0));
        }, cancellationToken);
    }

    private static Element Column(Model model, string columnName)
        => model.Elements
            .Single(e => e.Type == MariaDbElementTypes.SqlTable)
            .Relationships.Single(r => r.Name == MariaDbRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single(c => SqlName.UnqualifiedOf((string)c.Name!) == columnName);

    private static string? DefaultOf(Model model, string columnName)
        => Column(model, columnName).GetProperty<string>(MariaDbPropertyNames.DefaultValue);
}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class MariaDbColumnFidelityTestsMariaDb(MariaDbFixture fixture)
    : MariaDbColumnFidelityTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbColumnFidelityTestsMySql(MySqlFixture fixture)
    : MariaDbColumnFidelityTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

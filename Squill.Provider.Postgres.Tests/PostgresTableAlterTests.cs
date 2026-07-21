using System.Text.RegularExpressions;
using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Fast, Docker-free tests over ALTER / table-rebuild diffing (issues #29, #32). A
/// "before" and an "after" schema are each parsed into a model with the parser-based
/// builder (no database required), diffed, and the resulting deltas and generated SQL
/// are asserted. End-to-end behavior against real Postgres is covered by the
/// integration tests.
/// </summary>
public class PostgresTableAlterTests
{
    // Diffs a desired ("source") schema against a current ("target") schema, exactly as a
    // deploy would: the DACPAC's model vs. the database's current model.
    // Most tests here exercise rebuild/drop mechanics, which can be data-losing; block-on-
    // data-loss is turned off by default so those tests see the delta rather than the
    // block. The data-loss block itself is covered by dedicated tests (PostgresDropAndData
    // LossTests). allowTableRebuild always applies, so a test can assert it is enforced.
    private static async Task<SchemaComparison> CompareAsync(
        string sourceSql, string targetSql, bool allowTableRebuild = true)
    {
        var provider = new PostgresDatabaseProvider("Host=unused");

        var source = await BuildModelAsync(sourceSql);
        var target = await BuildModelAsync(targetSql);

        var options = new DeployOptions
        {
            AllowTableRebuild = allowTableRebuild,
            BlockOnPossibleDataLoss = false,
        };

        return SchemaCompare.Compare(provider, source, target, options);
    }

    private static async Task<Model> BuildModelAsync(string sql)
    {
        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return await new ParserWorkspaceModelBuilder(workspace, parser).ExtractModelAsync();
    }

    [Fact]
    public void DiffTable_HashDiffersButColumnsIdentical_ThrowsClearDiagnostic()
    {
        // The over-broad rebuild fallback: if a table's hash differs but every column is
        // identical, there is no column change an ALTER can express and no data motion is
        // warranted. Rather than silently rewrite the whole table, refuse loudly. This
        // branch is effectively unreachable through normal diffing (a real column change
        // shows up as a column diff; a dependent index/PK/FK change doesn't touch the
        // table's hash), so the guard is a "should not happen" safety net.
        var source = BuildTableElementWithMarker("film", markerProperty: true);
        var target = BuildTableElementWithMarker("film", markerProperty: false);

        // Sanity: the two table elements have identical columns but different hashes.
        Assert.False(HashUtility.HashesEqual(source.Hash, target.Hash));

        var analyzer = new PostgresTableDiffAnalyzer();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            analyzer.DiffTable(source, target, new Model(), new Model(), allowTableRebuild: true));

        Assert.Contains("film", ex.Message);
        Assert.Contains("no column change", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // A table element with one column, optionally carrying a marker property so two such
    // tables have identical columns but different element hashes.
    private static Element BuildTableElementWithMarker(string tableName, bool markerProperty)
    {
        var table = PostgresModelFactory.CreateTable(SqlName.Object(tableName), "public");

        var column = new Element(PostgresElementTypes.SqlSimpleColumn)
        {
            Name = SqlName.Object(tableName).Child("id"),
        };

        table.Relationships.Add(
            new Relationship(PostgresRelationshipNames.Columns) { Entries = { column } });

        if (markerProperty)
        {
            // A property the table diff never inspects, so columns stay identical while the
            // element hash differs — exercising the fallback guard.
            table.Properties.Add(new Property("SquillTestMarker", true));
        }

        return table;
    }

    [Fact]
    public async Task IdenticalSchemas_ProduceNoDeltas()
    {
        const string sql = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);
""";

        var comparison = await CompareAsync(sql, sql);

        Assert.Empty(comparison.Deltas);
    }

    [Fact]
    public async Task AddColumnAtEnd_EmitsAddColumn()
    {
        const string target = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);
""";
        const string source = """
CREATE TABLE film
(
    film_id     integer PRIMARY KEY,
    title       varchar(255) NOT NULL,
    description text NULL
);
""";

        var comparison = await CompareAsync(source, target);

        var alter = Assert.IsType<AlterDelta>(Assert.Single(comparison.Deltas));
        var change = Assert.Single(alter.ColumnChanges);
        Assert.Equal(ColumnChangeKind.Add, change.Kind);

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("ALTER TABLE \"film\" ADD COLUMN \"description\" text", sql);
    }

    [Fact]
    public async Task DropColumn_EmitsDropColumn()
    {
        const string target = """
CREATE TABLE film
(
    film_id     integer PRIMARY KEY,
    title       varchar(255) NOT NULL,
    description text NULL
);
""";
        const string source = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);
""";

        var comparison = await CompareAsync(source, target);

        var alter = Assert.IsType<AlterDelta>(Assert.Single(comparison.Deltas));
        var change = Assert.Single(alter.ColumnChanges);
        Assert.Equal(ColumnChangeKind.Drop, change.Kind);

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("ALTER TABLE \"film\" DROP COLUMN \"description\";", sql);
    }

    [Fact]
    public async Task WidenColumnType_EmitsAlterColumnType()
    {
        const string target = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(100) NOT NULL
);
""";
        const string source = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);
""";

        var comparison = await CompareAsync(source, target);

        var alter = Assert.IsType<AlterDelta>(Assert.Single(comparison.Deltas));
        var change = Assert.Single(alter.ColumnChanges);
        Assert.Equal(ColumnChangeKind.Alter, change.Kind);

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("ALTER TABLE \"film\" ALTER COLUMN \"title\" TYPE varchar(255);", sql);
    }

    [Fact]
    public async Task MakeColumnNullable_EmitsDropNotNull()
    {
        const string target = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);
""";
        const string source = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NULL
);
""";

        var comparison = await CompareAsync(source, target);

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("ALTER TABLE \"film\" ALTER COLUMN \"title\" DROP NOT NULL;", sql);
    }

    [Fact]
    public async Task MakeColumnNotNull_EmitsSetNotNull()
    {
        const string target = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NULL
);
""";
        const string source = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);
""";

        var comparison = await CompareAsync(source, target);

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("ALTER TABLE \"film\" ALTER COLUMN \"title\" SET NOT NULL;", sql);
    }

    [Fact]
    public async Task InsertColumnBetweenExisting_RequiresRebuild()
    {
        const string target = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);
""";
        // description is inserted between film_id and title, changing the physical order.
        const string source = """
CREATE TABLE film
(
    film_id     integer PRIMARY KEY,
    description text NULL,
    title       varchar(255) NOT NULL
);
""";

        var comparison = await CompareAsync(source, target);

        var rebuild = Assert.IsType<RebuildTableDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal("film", rebuild.SourceElement.Name);

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        // The rebuild renames the old table aside, recreates the table, copies the shared
        // columns, and drops the original — all in one transaction.
        Assert.Contains("BEGIN;", sql);
        Assert.Contains("RENAME TO", sql);
        Assert.Contains("CREATE TABLE \"film\"", sql);
        Assert.Contains("INSERT INTO \"film\"", sql);
        // Only the columns common to both tables are carried across.
        Assert.Contains("\"film_id\"", sql);
        Assert.Contains("\"title\"", sql);
        Assert.DoesNotContain("\"description\"", sql.Split("INSERT INTO")[1]);
        Assert.Contains("DROP TABLE", sql);
        Assert.Contains("COMMIT;", sql);
    }

    [Fact]
    public async Task Rebuild_LongTableName_KeepsAsideNameWithinIdentifierLimit()
    {
        // A 60-char table name plus the rename-aside suffix would exceed Postgres's 63-byte
        // identifier limit and get silently truncated (risking a collision). The aside name
        // must stay within the limit.
        var longName = new string('a', 60);

        var target = $$"""
CREATE TABLE {{longName}}
(
    id    integer PRIMARY KEY,
    title varchar(255) NOT NULL
);
""";
        // Insert a column mid-table to force a rebuild.
        var source = $$"""
CREATE TABLE {{longName}}
(
    id       integer PRIMARY KEY,
    subtitle varchar(255) NULL,
    title    varchar(255) NOT NULL
);
""";

        var comparison = await CompareAsync(source, target);
        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        // Every rename-aside identifier (the ones this guard controls) must be at most 63
        // bytes so Postgres does not silently truncate it. Match the aside suffix.
        var asideNames = Regex.Matches(sql, "\"([^\"]*__squill_rebuild_old)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(asideNames);

        foreach (var asideName in asideNames)
        {
            Assert.True(
                System.Text.Encoding.UTF8.GetByteCount(asideName) <= 63,
                $"Aside identifier '{asideName}' exceeds the 63-byte limit.");
        }

        // The naive base + suffix would exceed the limit, so it must not appear verbatim.
        Assert.DoesNotContain($"\"{longName}__squill_rebuild_old\"", sql);
    }

    [Fact]
    public void RebuildAsideName_ShortName_UsesSuffixVerbatim()
    {
        Assert.Equal("film__squill_rebuild_old", PostgresScriptGenerator.RebuildAsideName("film"));
    }

    [Fact]
    public void RebuildAsideName_LongNames_StayWithinLimitAndStayDistinct()
    {
        var a = PostgresScriptGenerator.RebuildAsideName(new string('a', 60));
        var b = PostgresScriptGenerator.RebuildAsideName(new string('a', 59) + "b");

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(a) <= 63);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(b) <= 63);
        // Two distinct long names that share a truncated prefix must not collide.
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task InsertColumnBetweenExisting_WhenRebuildDisallowed_Throws()
    {
        const string target = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);
""";
        const string source = """
CREATE TABLE film
(
    film_id     integer PRIMARY KEY,
    description text NULL,
    title       varchar(255) NOT NULL
);
""";

        var ex = await Assert.ThrowsAsync<TableRebuildNotAllowedException>(
            () => CompareAsync(source, target, allowTableRebuild: false));

        Assert.Equal("film", ex.TableName);
    }

    [Fact]
    public async Task AddColumn_WhenRebuildDisallowed_StillWorks()
    {
        // Appending a column at the end is an in-place ALTER, not a rebuild, so it must
        // succeed even when table rebuilds are disallowed.
        const string target = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);
""";
        const string source = """
CREATE TABLE film
(
    film_id     integer PRIMARY KEY,
    title       varchar(255) NOT NULL,
    description text NULL
);
""";

        var comparison = await CompareAsync(source, target, allowTableRebuild: false);

        var alter = Assert.IsType<AlterDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal(ColumnChangeKind.Add, Assert.Single(alter.ColumnChanges).Kind);
    }

    [Fact]
    public async Task WidenColumnType_EmitsOnlyTypeClause_NotNullability()
    {
        // Only the type changed; nullability is NOT NULL in both, so no SET/DROP NOT NULL
        // clause should be emitted (a redundant one would needlessly rewrite the table).
        const string target = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(100) NOT NULL
);
""";
        const string source = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);
""";

        var comparison = await CompareAsync(source, target);
        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("ALTER COLUMN \"title\" TYPE varchar(255)", sql);
        Assert.DoesNotContain("NOT NULL", sql);
    }

    [Fact]
    public async Task MakeColumnNullable_EmitsOnlyNullabilityClause_NotType()
    {
        // Only nullability changed; the type is identical, so no redundant TYPE clause.
        const string target = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);
""";
        const string source = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NULL
);
""";

        var comparison = await CompareAsync(source, target);
        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("ALTER COLUMN \"title\" DROP NOT NULL", sql);
        Assert.DoesNotContain("TYPE", sql);
    }

    [Fact]
    public async Task AddIdentityToExistingColumn_RequiresRebuild()
    {
        // Turning a plain column into a GENERATED AS IDENTITY column can't be done with the
        // ALTER path's TYPE/nullability clauses, so it must rebuild.
        const string target = """
CREATE TABLE widgets
(
    id integer PRIMARY KEY
);
""";
        const string source = """
CREATE TABLE widgets
(
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY
);
""";

        var comparison = await CompareAsync(source, target);

        Assert.IsType<RebuildTableDelta>(Assert.Single(comparison.Deltas));
    }

    [Fact]
    public async Task ChangeIdentityGeneration_RequiresRebuild()
    {
        const string target = """
CREATE TABLE widgets
(
    id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY
);
""";
        const string source = """
CREATE TABLE widgets
(
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY
);
""";

        var comparison = await CompareAsync(source, target);

        Assert.IsType<RebuildTableDelta>(Assert.Single(comparison.Deltas));
    }

    [Fact]
    public async Task ChangeIdentitySequenceOptions_RequiresRebuild()
    {
        const string target = """
CREATE TABLE widgets
(
    id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY
);
""";
        const string source = """
CREATE TABLE widgets
(
    id integer GENERATED BY DEFAULT AS IDENTITY (START WITH 100 INCREMENT BY 5) PRIMARY KEY
);
""";

        var comparison = await CompareAsync(source, target);

        Assert.IsType<RebuildTableDelta>(Assert.Single(comparison.Deltas));
    }

    [Fact]
    public async Task Rebuild_WithInboundForeignKey_DropsAndRecreatesTheReferencingFk()
    {
        // customer is referenced by orders; rebuilding customer (by inserting a column
        // mid-table) can't drop the renamed-aside table while orders' FK points at it, so
        // the referencing FK is dropped before the rebuild and recreated after, inside the
        // same transaction.
        const string target = """
CREATE TABLE customer
(
    customer_id integer PRIMARY KEY,
    email       varchar(320) NOT NULL
);

CREATE TABLE orders
(
    order_id    integer PRIMARY KEY,
    customer_id integer NOT NULL REFERENCES customer (customer_id)
);
""";
        const string source = """
CREATE TABLE customer
(
    customer_id integer PRIMARY KEY,
    full_name   varchar(200) NULL,
    email       varchar(320) NOT NULL
);

CREATE TABLE orders
(
    order_id    integer PRIMARY KEY,
    customer_id integer NOT NULL REFERENCES customer (customer_id)
);
""";

        var comparison = await CompareAsync(source, target);

        var rebuild = Assert.Single(comparison.Deltas.OfType<RebuildTableDelta>());
        Assert.Equal("customer", rebuild.SourceElement.Name);

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        // The referencing FK on orders is dropped before the rebuild and re-added after.
        Assert.Contains("ALTER TABLE \"orders\" DROP CONSTRAINT \"orders_customer_id_fkey\";", sql);
        Assert.Contains("ALTER TABLE \"orders\" ADD CONSTRAINT \"orders_customer_id_fkey\" FOREIGN KEY (\"customer_id\") REFERENCES \"customer\" (\"customer_id\")", sql);

        // The drop must precede the rebuild's rename, and the recreate must follow the drop
        // of the old table — all within the single BEGIN/COMMIT.
        var dropFkIndex = sql.IndexOf("DROP CONSTRAINT", StringComparison.Ordinal);
        var renameIndex = sql.IndexOf("RENAME TO", StringComparison.Ordinal);
        var addFkIndex = sql.IndexOf("ADD CONSTRAINT", StringComparison.Ordinal);
        var dropOldTableIndex = sql.IndexOf("DROP TABLE", StringComparison.Ordinal);

        Assert.True(dropFkIndex < renameIndex, "FK must be dropped before the rename-aside");
        Assert.True(dropOldTableIndex < addFkIndex, "FK must be re-added after the old table is dropped");
        Assert.True(
            sql.IndexOf("BEGIN;", StringComparison.Ordinal) < dropFkIndex
                && addFkIndex < sql.LastIndexOf("COMMIT;", StringComparison.Ordinal),
            "FK reconciliation must happen inside the rebuild transaction");
    }

    [Fact]
    public async Task Rebuild_WithIdentityColumn_AdvancesSequenceAfterCopy()
    {
        const string target = """
CREATE TABLE customer
(
    customer_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    email       varchar(320) NOT NULL
);
""";
        const string source = """
CREATE TABLE customer
(
    customer_id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    full_name   varchar(200) NULL,
    email       varchar(320) NOT NULL
);
""";

        var comparison = await CompareAsync(source, target);
        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        // The copy must override the generated identity, and the sequence must be advanced
        // past the copied values so future inserts don't collide.
        Assert.Contains("OVERRIDING SYSTEM VALUE", sql);
        Assert.Contains("setval(pg_get_serial_sequence(", sql);
        Assert.Contains("MAX(\"customer_id\")", sql);
    }

    [Fact]
    public async Task NewTable_AlongsideUnchangedTable_EmitsOnlyCreate()
    {
        const string target = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY
);
""";
        const string source = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY
);

CREATE TABLE actor
(
    actor_id integer PRIMARY KEY
);
""";

        var comparison = await CompareAsync(source, target);

        var create = Assert.IsType<CreateDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal("actor", create.Element.Name);
    }

    // --- Changed standalone indexes (issue #36) ---
    // An index is a separate top-level element, so a change to its definition on an
    // otherwise-unchanged table isn't a table diff. Postgres has no ALTER INDEX for a
    // definition change, so the fix is drop-and-recreate (a RecreateDelta). Indexes hold
    // no data, so this is never a data-loss operation.

    [Fact]
    public async Task ChangedIndexColumns_OnUnchangedTable_EmitsRecreate()
    {
        const string target = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL,
    rating  varchar(10)
);

CREATE INDEX idx_film_title ON film (title);
""";
        const string source = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL,
    rating  varchar(10)
);

CREATE INDEX idx_film_title ON film (title, rating);
""";

        var comparison = await CompareAsync(source, target);

        // The table's own columns are identical, so the only change is the index — a
        // single RecreateDelta, not a table ALTER or rebuild.
        var recreate = Assert.IsType<RecreateDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal("idx_film_title", recreate.SourceElement.Name);
        Assert.Equal("idx_film_title", recreate.TargetElement.Name);
    }

    [Fact]
    public async Task ChangedIndexColumns_GeneratesDropThenCreate()
    {
        const string target = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL,
    rating  varchar(10)
);

CREATE INDEX idx_film_title ON film (title);
""";
        const string source = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL,
    rating  varchar(10)
);

CREATE INDEX idx_film_title ON film (title, rating);
""";

        var comparison = await CompareAsync(source, target);
        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        // Drop the old index (idempotently) before creating the new shape, and the DROP
        // must precede the CREATE.
        Assert.Contains("DROP INDEX IF EXISTS \"idx_film_title\";", sql);
        Assert.Contains("CREATE INDEX \"idx_film_title\" ON \"film\" (\"title\", \"rating\");", sql);
        Assert.True(
            sql.IndexOf("DROP INDEX", StringComparison.Ordinal)
                < sql.IndexOf("CREATE INDEX", StringComparison.Ordinal),
            "DROP INDEX must come before CREATE INDEX");
    }

    [Fact]
    public async Task ChangedIndexUniqueness_EmitsRecreate()
    {
        const string target = """
CREATE TABLE account
(
    account_id integer PRIMARY KEY,
    email      varchar(255) NOT NULL
);

CREATE INDEX idx_account_email ON account (email);
""";
        const string source = """
CREATE TABLE account
(
    account_id integer PRIMARY KEY,
    email      varchar(255) NOT NULL
);

CREATE UNIQUE INDEX idx_account_email ON account (email);
""";

        var comparison = await CompareAsync(source, target);
        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.IsType<RecreateDelta>(Assert.Single(comparison.Deltas));
        Assert.Contains("DROP INDEX IF EXISTS \"idx_account_email\";", sql);
        Assert.Contains("CREATE UNIQUE INDEX \"idx_account_email\" ON \"account\" (\"email\");", sql);
    }

    [Fact]
    public async Task UnchangedIndex_ProducesNoDelta()
    {
        const string sql = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);

CREATE INDEX idx_film_title ON film (title);
""";

        var comparison = await CompareAsync(sql, sql);

        Assert.Empty(comparison.Deltas);
    }

    [Fact]
    public async Task ChangedIndex_IsNotTreatedAsDataLoss()
    {
        const string target = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL,
    rating  varchar(10)
);

CREATE INDEX idx_film_title ON film (title);
""";
        const string source = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL,
    rating  varchar(10)
);

CREATE INDEX idx_film_title ON film (title, rating);
""";

        // Even with BlockOnPossibleDataLoss on, recreating an index must not be blocked —
        // an index carries no data.
        var provider = new PostgresDatabaseProvider("Host=unused");
        var sourceModel = await BuildModelAsync(source);
        var targetModel = await BuildModelAsync(target);

        var comparison = SchemaCompare.Compare(
            provider, sourceModel, targetModel,
            new DeployOptions { BlockOnPossibleDataLoss = true });

        Assert.IsType<RecreateDelta>(Assert.Single(comparison.Deltas));
        Assert.Empty(comparison.DataLossReasons);
    }

    // --- Column DEFAULT changes (issue #36) ---
    // A default is a column facet, altered in place with ALTER COLUMN SET/DROP DEFAULT —
    // no rebuild and no data motion.

    [Fact]
    public async Task AddColumnDefault_EmitsSetDefault()
    {
        const string target = """
CREATE TABLE widgets
(
    id    integer PRIMARY KEY,
    count integer NOT NULL
);
""";
        const string source = """
CREATE TABLE widgets
(
    id    integer PRIMARY KEY,
    count integer NOT NULL DEFAULT 0
);
""";

        var comparison = await CompareAsync(source, target);
        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        var alter = Assert.IsType<AlterDelta>(Assert.Single(comparison.Deltas));
        Assert.Contains(alter.ColumnChanges, c => c.ColumnName.EndsWith("count"));
        Assert.Contains("ALTER TABLE \"widgets\" ALTER COLUMN \"count\" SET DEFAULT 0;", sql);
    }

    [Fact]
    public async Task RemoveColumnDefault_EmitsDropDefault()
    {
        const string target = """
CREATE TABLE widgets
(
    id    integer PRIMARY KEY,
    count integer NOT NULL DEFAULT 0
);
""";
        const string source = """
CREATE TABLE widgets
(
    id    integer PRIMARY KEY,
    count integer NOT NULL
);
""";

        var comparison = await CompareAsync(source, target);
        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.IsType<AlterDelta>(Assert.Single(comparison.Deltas));
        Assert.Contains("ALTER TABLE \"widgets\" ALTER COLUMN \"count\" DROP DEFAULT;", sql);
    }

    [Fact]
    public async Task ChangeColumnDefault_EmitsSetDefault()
    {
        const string target = """
CREATE TABLE orders
(
    id     integer PRIMARY KEY,
    status varchar(20) NOT NULL DEFAULT 'active'
);
""";
        const string source = """
CREATE TABLE orders
(
    id     integer PRIMARY KEY,
    status varchar(20) NOT NULL DEFAULT 'pending'
);
""";

        var comparison = await CompareAsync(source, target);
        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.IsType<AlterDelta>(Assert.Single(comparison.Deltas));
        Assert.Contains("ALTER TABLE \"orders\" ALTER COLUMN \"status\" SET DEFAULT 'pending';", sql);
    }

    [Fact]
    public async Task SameColumnDefault_ProducesNoDelta()
    {
        const string sql = """
CREATE TABLE widgets
(
    id    integer PRIMARY KEY,
    count integer NOT NULL DEFAULT 0
);
""";

        var comparison = await CompareAsync(sql, sql);

        Assert.Empty(comparison.Deltas);
    }

    [Fact]
    public async Task NamedColumnDefault_IsRejectedWithClearError()
    {
        // Postgres accepts CONSTRAINT <name> DEFAULT <expr> but silently discards the name
        // (it is not stored, unlike SQL Server), so it could never round-trip. Reject it at
        // build time with a message that says so, rather than model a name that vanishes.
        // The builder wraps the rejection in a SqlSourceException carrying the source file.
        const string sql = """
CREATE TABLE widgets
(
    id    integer PRIMARY KEY,
    count integer NOT NULL CONSTRAINT df_count DEFAULT 0
);
""";

        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync(sql));

        Assert.Contains("named", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("default", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

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
    public async Task Rebuild_WithInboundForeignKey_IsRefused()
    {
        // customer is referenced by orders; rebuilding customer (by inserting a column
        // mid-table) can't drop the renamed-aside table while orders' FK points at it, so
        // the diff refuses rather than emit a script that fails mid-transaction.
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

        var ex = await Assert.ThrowsAsync<TableRebuildNotSupportedException>(
            () => CompareAsync(source, target));

        Assert.Equal("customer", ex.TableName);
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
}

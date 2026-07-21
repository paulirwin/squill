using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Fast, Docker-free tests over user-defined schema support (issue #37): schema as a
/// declared object, schema in element identity, and schema-qualified DDL. Models are
/// built from SQL with the parser-based builder and diffed with explicit options.
/// </summary>
public class PostgresSchemaTests
{
    private static async Task<SchemaComparison> CompareAsync(
        string sourceSql, string targetSql, DeployOptions? options = null)
    {
        var provider = new PostgresDatabaseProvider("Host=unused");
        var source = await BuildModelAsync(sourceSql);
        var target = await BuildModelAsync(targetSql);

        return SchemaCompare.Compare(
            provider, source, target,
            options ?? new DeployOptions { BlockOnPossibleDataLoss = false });
    }

    private static async Task<Model> BuildModelAsync(string sql)
    {
        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return (await new ParserWorkspaceModelBuilder(workspace, parser).ExtractModelAsync()).Model;
    }

    [Fact]
    public async Task CreateSchema_EmitsCreateSchemaDdl()
    {
        var comparison = await CompareAsync("CREATE SCHEMA staging;", "");

        var create = Assert.IsType<CreateDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal(PostgresElementTypes.SqlSchema, create.Element.Type);
        Assert.Equal("staging", create.Element.Name);

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS \"staging\";", sql);
    }

    [Fact]
    public async Task SchemaCreatedBeforeItsTable_RegardlessOfFileOrder()
    {
        // The table is written before the schema in the file; the deploy must still create
        // the schema first (dependency order), or the CREATE TABLE would fail.
        const string source = """
CREATE TABLE staging.event (id integer PRIMARY KEY);
CREATE SCHEMA staging;
""";

        var comparison = await CompareAsync(source, "");
        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        var schemaPos = sql.IndexOf("CREATE SCHEMA", StringComparison.Ordinal);
        var tablePos = sql.IndexOf("CREATE TABLE", StringComparison.Ordinal);

        Assert.True(schemaPos >= 0 && tablePos >= 0);
        Assert.True(schemaPos < tablePos, "The schema must be created before its table.");
    }

    [Fact]
    public async Task NonPublicTable_IsSchemaQualified()
    {
        var comparison = await CompareAsync("""
CREATE SCHEMA staging;
CREATE TABLE staging.event (id integer PRIMARY KEY);
""", "");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("CREATE TABLE \"staging\".\"event\"", sql);
    }

    [Fact]
    public async Task PublicTable_IsNotSchemaQualified()
    {
        // A public-schema table keeps its clean, unqualified form.
        var comparison = await CompareAsync("CREATE TABLE film (id integer PRIMARY KEY);", "");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("CREATE TABLE \"film\"", sql);
        Assert.DoesNotContain("\"public\".\"film\"", sql);
    }

    [Fact]
    public async Task SameNameDifferentSchema_AreDistinctObjects()
    {
        // Source declares staging.log; target has public.log. They must NOT be treated as
        // the same object — with drops enabled, public.log is dropped and staging.log is
        // created, rather than public.log masking the new table.
        const string source = """
CREATE SCHEMA staging;
CREATE TABLE staging.log (id integer PRIMARY KEY);
""";
        const string target = "CREATE TABLE log (id integer PRIMARY KEY);";

        var comparison = await CompareAsync(source, target,
            new DeployOptions { DropObjectsNotInSource = true, BlockOnPossibleDataLoss = false });

        // staging.log created (schema + table), public.log dropped.
        Assert.Contains(comparison.Deltas,
            d => d is CreateDelta c && c.Element.Type == PostgresElementTypes.SqlSchema);
        Assert.Contains(comparison.Deltas,
            d => d is CreateDelta c && c.Element.Type == PostgresElementTypes.SqlTable);
        Assert.Contains(comparison.Deltas, d => d is DropDelta);
    }

    [Fact]
    public async Task ExtraSchemaInTarget_Dropped_WhenOptedIn_AfterItsTables()
    {
        // Target has schema 'old' with a table; source has neither. With drops enabled,
        // the table is dropped before the schema (reverse of create order).
        const string source = "";
        const string target = """
CREATE SCHEMA old;
CREATE TABLE old.thing (id integer PRIMARY KEY);
""";

        var comparison = await CompareAsync(source, target,
            new DeployOptions { DropObjectsNotInSource = true, BlockOnPossibleDataLoss = false });

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        var tableDropPos = sql.IndexOf("DROP TABLE", StringComparison.Ordinal);
        var schemaDropPos = sql.IndexOf("DROP SCHEMA", StringComparison.Ordinal);

        Assert.True(tableDropPos >= 0 && schemaDropPos >= 0);
        Assert.True(tableDropPos < schemaDropPos, "The table must be dropped before its schema.");
        Assert.Contains("DROP SCHEMA IF EXISTS \"old\";", sql);
    }

    [Fact]
    public async Task NonPublicForeignKey_ReferencesQualifiedTable()
    {
        var comparison = await CompareAsync("""
CREATE SCHEMA staging;
CREATE TABLE staging.parent (id integer PRIMARY KEY);
CREATE TABLE staging.child
(
    id        integer PRIMARY KEY,
    parent_id integer NOT NULL REFERENCES staging.parent (id)
);
""", "");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        // The FK target must be schema-qualified so it resolves regardless of search_path.
        Assert.Contains("REFERENCES \"staging\".\"parent\"", sql);
    }

    [Fact]
    public async Task ForeignKeyToPublicTable_FromNonPublic_IsNotOverQualified()
    {
        // A reference written as `public.parent` normalizes to the bare `parent`, matching
        // an unqualified reference and the DB builder — so the FK's referenced-table name
        // round-trips. The emitted DDL therefore references the unqualified public table.
        var comparison = await CompareAsync("""
CREATE SCHEMA app;
CREATE TABLE parent (id integer PRIMARY KEY);
CREATE TABLE app.child
(
    id        integer PRIMARY KEY,
    parent_id integer NOT NULL REFERENCES public.parent (id)
);
""", "");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("REFERENCES \"parent\"", sql);
        Assert.DoesNotContain("REFERENCES \"public\".\"parent\"", sql);
    }

    [Fact]
    public async Task CreateSchemaPublic_IsIgnored()
    {
        // 'public' is not a declared object; declaring it must not produce a delta (else a
        // redeploy would never converge, since the extractor never emits public).
        var comparison = await CompareAsync("CREATE SCHEMA public;", "");

        Assert.Empty(comparison.Deltas);
    }

    [Fact]
    public async Task SameNameTablesDifferentSchemas_DoNotShareDependentIndexes()
    {
        // public.event and staging.event both have a same-named-table index; each table's
        // CreateDelta must carry only its own index, not the other's.
        var comparison = await CompareAsync("""
CREATE SCHEMA staging;
CREATE TABLE event (id integer PRIMARY KEY, a integer);
CREATE INDEX ix_public ON event (a);
CREATE TABLE staging.event (id integer PRIMARY KEY, b integer);
CREATE INDEX ix_staging ON staging.event (b);
""", "");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        // Each index appears exactly once (not duplicated onto both tables).
        Assert.Equal(1, CountOccurrences(sql, "\"ix_public\""));
        Assert.Equal(1, CountOccurrences(sql, "\"ix_staging\""));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    [Fact]
    public async Task IdenticalSchemas_ProduceNoDeltas()
    {
        const string sql = """
CREATE SCHEMA staging;
CREATE TABLE staging.event (id integer PRIMARY KEY);
""";

        var comparison = await CompareAsync(sql, sql);

        Assert.Empty(comparison.Deltas);
    }
}

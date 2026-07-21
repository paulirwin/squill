using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// A table must be created after any table its foreign keys reference. Model order alone
/// is not enough: elements are ordered by schema then name, so a referencing table can
/// easily precede its target (audit.book_change sorts before public.book).
/// </summary>
public class ForeignKeyCreateOrderTests
{
    private static async Task<string> ScriptAgainstEmptyAsync(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        var model = await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        var provider = new PostgresDatabaseProvider("Host=unused");

        return new PostgresScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, model, new Model()));
    }

    private static void AssertCreatedBefore(string sql, string first, string second)
    {
        var firstIndex = sql.IndexOf(first, StringComparison.Ordinal);
        var secondIndex = sql.IndexOf(second, StringComparison.Ordinal);

        Assert.True(firstIndex >= 0, $"Expected to find '{first}' in:\n{sql}");
        Assert.True(secondIndex >= 0, $"Expected to find '{second}' in:\n{sql}");
        Assert.True(
            firstIndex < secondIndex,
            $"Expected '{first}' before '{second}', but got:\n{sql}");
    }

    // The referenced table is declared second, so source order alone would create the
    // referencing table first and the FK would fail.
    [Fact]
    public async Task ReferencingTable_IsCreatedAfterItsTarget()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE TABLE book
            (
                book_id   integer PRIMARY KEY,
                author_id integer NOT NULL REFERENCES author (author_id)
            );

            CREATE TABLE author (author_id integer PRIMARY KEY);
            """);

        AssertCreatedBefore(sql, "CREATE TABLE \"author\"", "CREATE TABLE \"book\"");
    }

    // The case the sample project hit: a non-public schema sorts before "public", so the
    // referencing table came first even though its target lives in public.
    [Fact]
    public async Task CrossSchemaReference_CreatesTheTargetSchemasTableFirst()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE SCHEMA audit;

            CREATE TABLE audit.book_change
            (
                book_change_id integer PRIMARY KEY,
                book_id        integer NOT NULL REFERENCES public.book (book_id)
            );

            CREATE TABLE book (book_id integer PRIMARY KEY);
            """);

        AssertCreatedBefore(sql, "CREATE TABLE \"book\"", "CREATE TABLE \"audit\".\"book_change\"");
    }

    // A chain must be fully ordered, not just each pair.
    [Fact]
    public async Task TransitiveReferences_AreOrderedThroughTheChain()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE TABLE c (c_id integer PRIMARY KEY, b_id integer NOT NULL REFERENCES b (b_id));
            CREATE TABLE b (b_id integer PRIMARY KEY, a_id integer NOT NULL REFERENCES a (a_id));
            CREATE TABLE a (a_id integer PRIMARY KEY);
            """);

        AssertCreatedBefore(sql, "CREATE TABLE \"a\"", "CREATE TABLE \"b\"");
        AssertCreatedBefore(sql, "CREATE TABLE \"b\"", "CREATE TABLE \"c\"");
    }

    // A self-referencing table depends on itself, which must not be mistaken for a cycle
    // that stalls ordering — the FK is satisfied by the table's own creation.
    [Fact]
    public async Task SelfReferencingTable_IsCreated()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE TABLE employee
            (
                employee_id integer PRIMARY KEY,
                manager_id  integer NULL REFERENCES employee (employee_id)
            );
            """);

        Assert.Contains("CREATE TABLE \"employee\"", sql);
    }

    // Two tables referencing each other cannot be ordered. Deploying is still better than
    // failing to produce a script at all, so a cycle must not throw or drop a table.
    [Fact]
    public async Task MutuallyReferencingTables_AreBothStillCreated()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE TABLE husband
            (
                id        integer PRIMARY KEY,
                wife_id   integer NULL REFERENCES wife (id)
            );

            CREATE TABLE wife
            (
                id         integer PRIMARY KEY,
                husband_id integer NULL REFERENCES husband (id)
            );
            """);

        Assert.Contains("CREATE TABLE \"husband\"", sql);
        Assert.Contains("CREATE TABLE \"wife\"", sql);
    }

    // Ordering tables by dependency must not disturb the existing rank ordering that puts
    // schemas and extensions before the tables that live in or use them.
    [Fact]
    public async Task SchemaIsStillCreatedBeforeItsTables()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE SCHEMA audit;

            CREATE TABLE audit.entry
            (
                entry_id integer PRIMARY KEY,
                book_id  integer NOT NULL REFERENCES public.book (book_id)
            );

            CREATE TABLE book (book_id integer PRIMARY KEY);
            """);

        AssertCreatedBefore(sql, "CREATE SCHEMA", "CREATE TABLE");
    }
}

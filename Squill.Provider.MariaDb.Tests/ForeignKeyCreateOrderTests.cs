using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// A table must be created after the tables its foreign keys reference, and a circular
/// reference — which no create order can satisfy — must be broken by deferring the
/// constraint that closes the cycle.
/// </summary>
public class ForeignKeyCreateOrderTests
{
    private static async Task<string> ScriptAgainstEmptyAsync(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        var model = (await new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken)).Model;

        var provider = new MariaDbDatabaseProvider("Server=unused");

        return new MariaDbScriptGenerator()
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

    [Fact]
    public async Task ReferencingTable_IsCreatedAfterItsTarget()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE TABLE book
            (
                book_id   int NOT NULL PRIMARY KEY,
                author_id int NOT NULL,
                FOREIGN KEY (author_id) REFERENCES author (author_id)
            );

            CREATE TABLE author (author_id int NOT NULL PRIMARY KEY);
            """);

        AssertCreatedBefore(sql, "CREATE TABLE `author`", "CREATE TABLE `book`");
    }

    [Fact]
    public async Task TransitiveReferences_AreOrderedThroughTheChain()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE TABLE c (c_id int NOT NULL PRIMARY KEY, b_id int NOT NULL,
                FOREIGN KEY (b_id) REFERENCES b (b_id));
            CREATE TABLE b (b_id int NOT NULL PRIMARY KEY, a_id int NOT NULL,
                FOREIGN KEY (a_id) REFERENCES a (a_id));
            CREATE TABLE a (a_id int NOT NULL PRIMARY KEY);
            """);

        AssertCreatedBefore(sql, "CREATE TABLE `a`", "CREATE TABLE `b`");
        AssertCreatedBefore(sql, "CREATE TABLE `b`", "CREATE TABLE `c`");
    }

    // Two tables referencing each other cannot both carry their foreign key inline, so the
    // one that closes the cycle is added afterwards with ALTER TABLE.
    [Fact]
    public async Task MutualReference_DefersTheCycleBreakingConstraint()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE TABLE husband
            (
                id      int NOT NULL PRIMARY KEY,
                wife_id int NULL,
                FOREIGN KEY (wife_id) REFERENCES wife (id)
            );

            CREATE TABLE wife
            (
                id         int NOT NULL PRIMARY KEY,
                husband_id int NULL,
                FOREIGN KEY (husband_id) REFERENCES husband (id)
            );
            """);

        Assert.Contains("CREATE TABLE `husband`", sql);
        Assert.Contains("CREATE TABLE `wife`", sql);

        var lastCreate = sql.LastIndexOf("CREATE TABLE", StringComparison.Ordinal);
        var addConstraint = sql.IndexOf("ADD CONSTRAINT", StringComparison.Ordinal);

        Assert.True(addConstraint >= 0, $"Expected a deferred ADD CONSTRAINT in:\n{sql}");
        Assert.True(
            addConstraint > lastCreate,
            $"Expected the deferred constraint after both tables are created, but got:\n{sql}");

        // Only the edge that closes the cycle is deferred; the other stays inline.
        Assert.Equal(1, sql.Split("ADD CONSTRAINT").Length - 1);
    }

    [Fact]
    public async Task SelfReference_IsNotDeferred()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE TABLE employee
            (
                employee_id int NOT NULL PRIMARY KEY,
                manager_id  int NULL,
                FOREIGN KEY (manager_id) REFERENCES employee (employee_id)
            );
            """);

        Assert.Contains("CREATE TABLE `employee`", sql);
        Assert.DoesNotContain("ADD CONSTRAINT", sql);
    }

    [Fact]
    public async Task AcyclicReferences_AreNotDeferred()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE TABLE book (id int NOT NULL PRIMARY KEY, author_id int NOT NULL,
                FOREIGN KEY (author_id) REFERENCES author (id));
            CREATE TABLE author (id int NOT NULL PRIMARY KEY);
            """);

        Assert.DoesNotContain("ADD CONSTRAINT", sql);
    }
}

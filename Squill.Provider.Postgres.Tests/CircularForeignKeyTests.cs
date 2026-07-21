using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Two tables that reference each other cannot both be created with their foreign keys
/// inline — whichever is created first would reference a table that does not exist yet.
/// The cycle is broken by creating the tables without the offending constraints and adding
/// those back with ALTER TABLE afterwards.
/// </summary>
public class CircularForeignKeyTests
{
    private static async Task<string> ScriptAgainstEmptyAsync(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        var model = (await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken)).Model;

        var provider = new PostgresDatabaseProvider("Host=unused");

        return new PostgresScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, model, new Model()));
    }

    private const string MutualReference = """
        CREATE TABLE husband
        (
            id      integer PRIMARY KEY,
            wife_id integer NULL REFERENCES wife (id)
        );

        CREATE TABLE wife
        (
            id         integer PRIMARY KEY,
            husband_id integer NULL REFERENCES husband (id)
        );
        """;

    [Fact]
    public async Task MutualReference_CreatesBothTables()
    {
        var sql = await ScriptAgainstEmptyAsync(MutualReference);

        Assert.Contains("CREATE TABLE \"husband\"", sql);
        Assert.Contains("CREATE TABLE \"wife\"", sql);
    }

    // The constraint that closes the cycle must not be inline on the table that is created
    // first, because its target does not exist yet.
    [Fact]
    public async Task MutualReference_DefersTheCycleBreakingConstraint()
    {
        var sql = await ScriptAgainstEmptyAsync(MutualReference);

        var firstCreate = sql.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        var lastCreate = sql.LastIndexOf("CREATE TABLE", StringComparison.Ordinal);
        var addConstraint = sql.IndexOf("ADD CONSTRAINT", StringComparison.Ordinal);

        Assert.True(addConstraint >= 0, $"Expected a deferred ADD CONSTRAINT in:\n{sql}");
        Assert.True(
            addConstraint > lastCreate,
            $"Expected the deferred constraint after both tables are created, but got:\n{sql}");
        Assert.True(firstCreate < lastCreate, "Expected two CREATE TABLE statements");
    }

    // Only the constraint needed to break the cycle is deferred; the other direction can
    // still be created inline, since by then its target exists.
    [Fact]
    public async Task MutualReference_DefersOnlyOneConstraint()
    {
        var sql = await ScriptAgainstEmptyAsync(MutualReference);

        var deferred = sql.Split("ADD CONSTRAINT").Length - 1;

        Assert.Equal(1, deferred);
    }

    // A three-table cycle is the same problem one step removed.
    [Fact]
    public async Task ThreeTableCycle_IsBrokenAndAllTablesCreated()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE TABLE a (id integer PRIMARY KEY, c_id integer NULL REFERENCES c (id));
            CREATE TABLE b (id integer PRIMARY KEY, a_id integer NULL REFERENCES a (id));
            CREATE TABLE c (id integer PRIMARY KEY, b_id integer NULL REFERENCES b (id));
            """);

        Assert.Contains("CREATE TABLE \"a\"", sql);
        Assert.Contains("CREATE TABLE \"b\"", sql);
        Assert.Contains("CREATE TABLE \"c\"", sql);
        Assert.Contains("ADD CONSTRAINT", sql);
    }

    // A self-reference is a cycle of one, but needs no deferral: PostgreSQL accepts a
    // foreign key to the table being created in the same statement.
    [Fact]
    public async Task SelfReference_IsNotDeferred()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE TABLE employee
            (
                employee_id integer PRIMARY KEY,
                manager_id  integer NULL REFERENCES employee (employee_id)
            );
            """);

        Assert.Contains("CREATE TABLE \"employee\"", sql);
        Assert.DoesNotContain("ADD CONSTRAINT", sql);
    }

    // An acyclic schema is ordered, not deferred — deferral is a last resort, since an
    // inline constraint is clearer and is enforced from the moment the table exists.
    [Fact]
    public async Task AcyclicReferences_AreNotDeferred()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE TABLE book (id integer PRIMARY KEY, author_id integer NOT NULL REFERENCES author (id));
            CREATE TABLE author (id integer PRIMARY KEY);
            """);

        Assert.DoesNotContain("ADD CONSTRAINT", sql);
    }
}

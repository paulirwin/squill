using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Fast, Docker-free tests over function script generation (issue #74). Models are built with
/// the parser-based model builder and diffed against an empty target, so every function
/// becomes a CreateDelta. Mirrors <see cref="MariaDbProcedureScriptTests"/>.
/// </summary>
public class MariaDbFunctionScriptTests
{
    private static async Task<Model> BuildModelAsync(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return (await new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken)).Model;
    }

    private static async Task<string> ScriptAgainstEmptyAsync(string sql)
    {
        var model = await BuildModelAsync(sql);
        var provider = new MariaDbDatabaseProvider("Server=unused");

        return new MariaDbScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, model, new Model()));
    }

    [Fact]
    public async Task CreateFunction_ScriptsNameArgumentsReturnTypeAndBody()
    {
        var script = await ScriptAgainstEmptyAsync(
            "CREATE FUNCTION add_tax(price DECIMAL(10,2)) RETURNS DECIMAL(10,2) "
            + "DETERMINISTIC RETURN price * 1.1;");

        // A function parameter carries no mode keyword in DDL — it is always IN — even though
        // the model stores it with an "IN " prefix to hash-match the catalog.
        Assert.Contains(
            "CREATE FUNCTION `add_tax`(price decimal(10,2)) RETURNS decimal(10,2)",
            script);
        Assert.DoesNotContain("IN price", script);
        Assert.Contains("RETURN price * 1.1", script);
    }

    [Fact]
    public async Task CreateFunction_NoParameters_ScriptsEmptyParens()
    {
        var script = await ScriptAgainstEmptyAsync("CREATE FUNCTION answer() RETURNS INT RETURN 42;");

        Assert.Contains("CREATE FUNCTION `answer`() RETURNS int", script);
    }

    [Fact]
    public async Task CreateFunction_OmitsDefaultCharacteristics()
    {
        var script = await ScriptAgainstEmptyAsync("CREATE FUNCTION f() RETURNS INT RETURN 1;");

        Assert.DoesNotContain("DETERMINISTIC", script);
        Assert.DoesNotContain("SQL SECURITY", script);
        Assert.DoesNotContain("CONTAINS SQL", script);
    }

    [Fact]
    public async Task CreateFunction_ScriptsNonDefaultCharacteristics()
    {
        var script = await ScriptAgainstEmptyAsync(
            """
            CREATE FUNCTION f() RETURNS INT
              DETERMINISTIC
              READS SQL DATA
              SQL SECURITY INVOKER
              RETURN 1;
            """);

        Assert.Contains("DETERMINISTIC", script);
        Assert.Contains("READS SQL DATA", script);
        Assert.Contains("SQL SECURITY INVOKER", script);
    }

    [Fact]
    public async Task CreateFunction_IsScriptedAfterTables()
    {
        // A function body may reference any table and is not parsed for dependencies, so
        // functions must be created last. The source deliberately declares it first.
        var script = await ScriptAgainstEmptyAsync(
            """
            CREATE FUNCTION widget_count() RETURNS INT
              RETURN (SELECT COUNT(*) FROM widgets);

            CREATE TABLE widgets (id INT NOT NULL PRIMARY KEY);
            """);

        Assert.True(
            script.IndexOf("CREATE TABLE", StringComparison.Ordinal)
                < script.IndexOf("CREATE FUNCTION", StringComparison.Ordinal),
            $"The function should be created after the table.\n{script}");
    }

    [Fact]
    public async Task ChangedFunctionBody_IsDroppedAndRecreated()
    {
        // Unlike PostgreSQL there is no portable in-place redefinition: MySQL has no
        // CREATE OR REPLACE FUNCTION and neither engine can ALTER a routine's body.
        var before = await BuildModelAsync("CREATE FUNCTION f() RETURNS INT RETURN 1;");
        var after = await BuildModelAsync("CREATE FUNCTION f() RETURNS INT RETURN 2;");

        var provider = new MariaDbDatabaseProvider("Server=unused");
        var comparison = SchemaCompare.Compare(provider, after, before);

        Assert.IsType<RecreateDelta>(Assert.Single(comparison.Deltas));

        var script = new MariaDbScriptGenerator().GenerateScript(comparison);

        Assert.Contains("DROP FUNCTION IF EXISTS `f`;", script);
        Assert.Contains("CREATE FUNCTION `f`() RETURNS int", script);
        Assert.Contains("RETURN 2", script);

        Assert.True(
            script.IndexOf("DROP FUNCTION", StringComparison.Ordinal)
                < script.IndexOf("CREATE FUNCTION", StringComparison.Ordinal),
            $"The drop must precede the create.\n{script}");
    }

    [Fact]
    public async Task ChangedReturnType_IsRecreated()
    {
        var before = await BuildModelAsync("CREATE FUNCTION f() RETURNS INT RETURN 1;");
        var after = await BuildModelAsync("CREATE FUNCTION f() RETURNS BIGINT RETURN 1;");

        var provider = new MariaDbDatabaseProvider("Server=unused");
        var comparison = SchemaCompare.Compare(provider, after, before);

        Assert.IsType<RecreateDelta>(Assert.Single(comparison.Deltas));
    }

    [Fact]
    public async Task UnchangedFunction_ProducesNoScript()
    {
        var model = await BuildModelAsync(
            "CREATE FUNCTION f(a INT) RETURNS INT DETERMINISTIC RETURN a + 1;");
        var other = await BuildModelAsync(
            "CREATE FUNCTION f(a INT) RETURNS INT DETERMINISTIC RETURN a + 1;");

        var provider = new MariaDbDatabaseProvider("Server=unused");

        Assert.Empty(SchemaCompare.Compare(provider, model, other).Deltas);
    }

    [Fact]
    public async Task DroppedFunction_IsScriptedWithoutASignature()
    {
        // Neither engine allows overloading, so the name alone identifies the function.
        var target = await BuildModelAsync("CREATE FUNCTION f(a INT) RETURNS INT RETURN a;");

        var provider = new MariaDbDatabaseProvider("Server=unused");
        var comparison = SchemaCompare.Compare(
            provider, new Model(), target, new DeployOptions { DropObjectsNotInSource = true });

        var script = new MariaDbScriptGenerator().GenerateScript(comparison);

        Assert.Contains("DROP FUNCTION IF EXISTS `f`;", script);
    }

    [Fact]
    public async Task CreateFunction_BeginEndBodyIsOneStatement()
    {
        var script = await ScriptAgainstEmptyAsync(
            """
            CREATE FUNCTION f() RETURNS INT
            BEGIN
              DECLARE x INT;
              SET x = 1;
              RETURN x;
            END;
            """);

        Assert.Contains("END", script);
        Assert.DoesNotContain("END;", script);
    }
}

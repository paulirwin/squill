using Squill.Core;
using Squill.TestFramework;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Fast, Docker-free tests over procedure script generation (issue #41). Models are built
/// with the parser-based model builder and diffed against an empty target, so every
/// procedure becomes a CreateDelta.
/// </summary>
public class MariaDbProcedureScriptTests
{
    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser()),
            TestContext.Current.CancellationToken);

    private static async Task<string> ScriptAgainstEmptyAsync(string sql)
    {
        var model = await BuildModelAsync(sql);
        var provider = new MariaDbDatabaseProvider("Server=unused");

        return new MariaDbScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, model, new Model()));
    }

    [Fact]
    public async Task CreateProcedure_ScriptsNameArgumentsAndBody()
    {
        var script = await ScriptAgainstEmptyAsync(
            """
            CREATE PROCEDURE add_widget(IN widget_name VARCHAR(50))
            BEGIN
              INSERT INTO widgets (name) VALUES (widget_name);
            END;
            """);

        Assert.Contains("CREATE PROCEDURE `add_widget`(IN widget_name varchar(50))", script);
        Assert.Contains("INSERT INTO widgets (name) VALUES (widget_name);", script);
    }

    [Fact]
    public async Task CreateProcedure_OmitsDefaultCharacteristics()
    {
        var script = await ScriptAgainstEmptyAsync("CREATE PROCEDURE p() SELECT 1;");

        Assert.DoesNotContain("DETERMINISTIC", script);
        Assert.DoesNotContain("SQL SECURITY", script);
        Assert.DoesNotContain("CONTAINS SQL", script);
    }

    [Fact]
    public async Task CreateProcedure_ScriptsNonDefaultCharacteristics()
    {
        var script = await ScriptAgainstEmptyAsync(
            """
            CREATE PROCEDURE p()
              DETERMINISTIC
              MODIFIES SQL DATA
              SQL SECURITY INVOKER
              DELETE FROM t;
            """);

        Assert.Contains("DETERMINISTIC", script);
        Assert.Contains("MODIFIES SQL DATA", script);
        Assert.Contains("SQL SECURITY INVOKER", script);
    }

    [Fact]
    public async Task CreateProcedure_IsScriptedAfterTables()
    {
        // A procedure body may reference any table and is not parsed for dependencies, so
        // procedures must be created last. The source deliberately declares it first.
        var script = await ScriptAgainstEmptyAsync(
            """
            CREATE PROCEDURE add_widget(IN a INT)
              INSERT INTO widgets (id) VALUES (a);

            CREATE TABLE widgets (id INT NOT NULL PRIMARY KEY);
            """);

        Assert.True(
            script.IndexOf("CREATE TABLE", StringComparison.Ordinal)
                < script.IndexOf("CREATE PROCEDURE", StringComparison.Ordinal),
            $"The procedure should be created after the table.\n{script}");
    }

    [Fact]
    public async Task ChangedProcedureBody_IsDroppedAndRecreated()
    {
        // Unlike PostgreSQL there is no portable in-place redefinition: MySQL has no
        // CREATE OR REPLACE PROCEDURE and neither engine can ALTER a routine's body.
        var before = await BuildModelAsync("CREATE PROCEDURE p() SELECT 1;");
        var after = await BuildModelAsync("CREATE PROCEDURE p() SELECT 2;");

        var provider = new MariaDbDatabaseProvider("Server=unused");
        var comparison = SchemaCompare.Compare(provider, after, before);

        Assert.IsType<RecreateDelta>(Assert.Single(comparison.Deltas));

        var script = new MariaDbScriptGenerator().GenerateScript(comparison);

        Assert.Contains("DROP PROCEDURE IF EXISTS `p`;", script);
        Assert.Contains("CREATE PROCEDURE `p`()", script);
        Assert.Contains("SELECT 2", script);

        Assert.True(
            script.IndexOf("DROP PROCEDURE", StringComparison.Ordinal)
                < script.IndexOf("CREATE PROCEDURE", StringComparison.Ordinal),
            $"The drop must precede the create.\n{script}");
    }

    [Fact]
    public async Task ChangedProcedureCharacteristic_IsRecreated()
    {
        var before = await BuildModelAsync("CREATE PROCEDURE p() SELECT 1;");
        var after = await BuildModelAsync("CREATE PROCEDURE p() DETERMINISTIC SELECT 1;");

        var provider = new MariaDbDatabaseProvider("Server=unused");
        var comparison = SchemaCompare.Compare(provider, after, before);

        Assert.IsType<RecreateDelta>(Assert.Single(comparison.Deltas));
    }

    [Fact]
    public async Task UnchangedProcedure_ProducesNoScript()
    {
        var model = await BuildModelAsync(
            """
            CREATE PROCEDURE p(IN a INT)
            BEGIN
              SELECT a;
            END;
            """);

        var other = await BuildModelAsync(
            """
            CREATE PROCEDURE p(IN a INT)
            BEGIN
              SELECT a;
            END;
            """);

        var provider = new MariaDbDatabaseProvider("Server=unused");

        Assert.Empty(SchemaCompare.Compare(provider, model, other).Deltas);
    }

    [Fact]
    public async Task DroppedProcedure_IsScriptedWithoutASignature()
    {
        // Neither engine allows overloading, so the name alone identifies the procedure.
        var target = await BuildModelAsync("CREATE PROCEDURE p(IN a INT) SELECT a;");

        var provider = new MariaDbDatabaseProvider("Server=unused");
        var comparison = SchemaCompare.Compare(
            provider, new Model(), target, new DeployOptions { DropObjectsNotInSource = true });

        var script = new MariaDbScriptGenerator().GenerateScript(comparison);

        Assert.Contains("DROP PROCEDURE IF EXISTS `p`;", script);
    }

    [Fact]
    public async Task CreateProcedure_BodyWithSemicolonsIsOneStatement()
    {
        // Each delta is sent to the server as a single command, so a BEGIN ... END body
        // needs no DELIMITER handling — but the generated statement must not gain a
        // trailing semicolon that would terminate it early.
        var script = await ScriptAgainstEmptyAsync(
            """
            CREATE PROCEDURE p()
            BEGIN
              SELECT 1;
              SELECT 2;
            END;
            """);

        Assert.Contains("END", script);
        Assert.DoesNotContain("END;", script);
    }
}

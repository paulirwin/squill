using Squill.Core;
using Squill.TestFramework;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Fast, Docker-free tests over trigger script generation (issue #100). Models are built with
/// the parser-based model builder and diffed against an empty target, so every trigger becomes
/// a CreateDelta. Mirrors <see cref="MariaDbFunctionScriptTests"/>.
/// </summary>
public class MariaDbTriggerScriptTests
{
    private const string Tables =
        "CREATE TABLE film (film_id INT PRIMARY KEY, title VARCHAR(50));\n"
        + "CREATE TABLE film_text (film_id INT PRIMARY KEY, title VARCHAR(50));\n";

    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser(), new MariaDb12DatabaseSchemaProvider()),
            TestContext.Current.CancellationToken);

    private static async Task<string> ScriptAgainstEmptyAsync(string triggerSql)
    {
        var model = await BuildModelAsync(Tables + triggerSql);
        var provider = new MariaDbDatabaseProvider("Server=unused");

        return new MariaDbScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, model, new Model()));
    }

    [Fact]
    public async Task CreateTrigger_ScriptsNameTimingEventTableAndBody()
    {
        var script = await ScriptAgainstEmptyAsync(
            """
            CREATE TRIGGER ins_film AFTER INSERT ON film
            FOR EACH ROW
            BEGIN
              INSERT INTO film_text (film_id, title) VALUES (NEW.film_id, NEW.title);
            END;
            """);

        Assert.Contains(
            "CREATE TRIGGER `ins_film` AFTER INSERT ON `film` FOR EACH ROW",
            script);
        Assert.Contains(
            "INSERT INTO film_text (film_id, title) VALUES (NEW.film_id, NEW.title);",
            script);
    }

    [Fact]
    public async Task CreateTrigger_ScriptsBeforeUpdate()
    {
        var script = await ScriptAgainstEmptyAsync(
            "CREATE TRIGGER t BEFORE UPDATE ON film FOR EACH ROW SET NEW.title = NEW.title;");

        Assert.Contains("CREATE TRIGGER `t` BEFORE UPDATE ON `film` FOR EACH ROW", script);
    }

    [Fact]
    public async Task CreateTrigger_ScriptsAfterDelete()
    {
        var script = await ScriptAgainstEmptyAsync(
            "CREATE TRIGGER t AFTER DELETE ON film FOR EACH ROW "
            + "DELETE FROM film_text WHERE film_id = OLD.film_id;");

        Assert.Contains("CREATE TRIGGER `t` AFTER DELETE ON `film` FOR EACH ROW", script);
        Assert.Contains("DELETE FROM film_text WHERE film_id = OLD.film_id", script);
    }

    [Fact]
    public async Task DropTrigger_NamesTheTriggerAloneWithoutTable()
    {
        // A trigger whose definition changed is dropped and recreated. DROP TRIGGER names the
        // trigger alone — no table qualifier, which the syntax does not accept.
        var model = await BuildModelAsync(
            Tables + "CREATE TRIGGER t AFTER INSERT ON film FOR EACH ROW SET @x = 1;");
        var target = await BuildModelAsync(
            Tables + "CREATE TRIGGER t AFTER INSERT ON film FOR EACH ROW SET @x = 2;");

        var provider = new MariaDbDatabaseProvider("Server=unused");
        var script = new MariaDbScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, model, target));

        Assert.Contains("DROP TRIGGER IF EXISTS `t`;", script);
        Assert.Contains("CREATE TRIGGER `t` AFTER INSERT ON `film` FOR EACH ROW", script);
    }

    // ---- Firing order and DEFINER (issue #215) ----

    [Fact]
    public async Task Script_EmitsFollowsForEveryTriggerAfterTheFirst()
    {
        var script = await ScriptAgainstEmptyAsync(
            "CREATE TRIGGER a BEFORE INSERT ON film FOR EACH ROW SET @x = 1;\n"
            + "CREATE TRIGGER b BEFORE INSERT ON film FOR EACH ROW FOLLOWS a SET @x = 2;");

        // The first in the group carries no clause; the second names the one before it.
        Assert.Contains("CREATE TRIGGER `a` BEFORE INSERT ON `film` FOR EACH ROW\n", script);
        Assert.Contains("CREATE TRIGGER `b` BEFORE INSERT ON `film` FOR EACH ROW FOLLOWS `a`", script);
    }

    [Fact]
    public async Task Script_CreatesEachTriggerAfterTheOneItFollows()
    {
        // A FOLLOWS naming a trigger that does not exist yet is an error on both engines, so
        // the CREATE for a predecessor must be scripted first even when its name sorts later.
        var script = await ScriptAgainstEmptyAsync(
            "CREATE TRIGGER a_trig BEFORE INSERT ON film FOR EACH ROW SET @x = 1;\n"
            + "CREATE TRIGGER z_trig BEFORE INSERT ON film FOR EACH ROW PRECEDES a_trig SET @x = 2;");

        Assert.True(
            script.IndexOf("CREATE TRIGGER `z_trig`", StringComparison.Ordinal)
            < script.IndexOf("CREATE TRIGGER `a_trig`", StringComparison.Ordinal),
            "A trigger must be created after the one it follows.");
        Assert.Contains("CREATE TRIGGER `a_trig` BEFORE INSERT ON `film` FOR EACH ROW FOLLOWS `z_trig`", script);
    }

    [Fact]
    public async Task Script_LoneTriggerHasNoFollowsClause()
    {
        var script = await ScriptAgainstEmptyAsync(
            "CREATE TRIGGER a BEFORE INSERT ON film FOR EACH ROW SET @x = 1;");

        Assert.DoesNotContain("FOLLOWS", script);
    }

    [Fact]
    public async Task Script_EmitsDefiner()
    {
        var script = await ScriptAgainstEmptyAsync(
            "CREATE DEFINER = 'alice'@'%' TRIGGER a BEFORE INSERT ON film "
            + "FOR EACH ROW SET @x = 1;");

        Assert.Contains("CREATE DEFINER = `alice`@`%` TRIGGER `a` BEFORE INSERT ON `film`", script);
    }

    [Fact]
    public async Task Script_OmitsDefinerWhenNoneWasDeclared()
    {
        // An undeclared definer means the deploying user, which is what omitting the clause
        // gives, so nothing is emitted.
        var script = await ScriptAgainstEmptyAsync(
            "CREATE TRIGGER a BEFORE INSERT ON film FOR EACH ROW SET @x = 1;");

        Assert.DoesNotContain("DEFINER", script);
    }
}

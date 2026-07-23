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
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser()),
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
}

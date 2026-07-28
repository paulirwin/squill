using Squill.Core;
using Squill.TestFramework;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Fast, Docker-free tests over event script generation (issue #122). Models are built with
/// the parser-based model builder and diffed against an empty target, so every event becomes
/// a CreateDelta. Mirrors <see cref="MariaDbTriggerScriptTests"/>.
/// </summary>
public class MariaDbEventScriptTests
{
    private const string Tables = "CREATE TABLE stats (n INT PRIMARY KEY);\n";

    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser(), new MariaDb12DatabaseSchemaProvider()),
            TestContext.Current.CancellationToken);

    private static async Task<string> ScriptAgainstEmptyAsync(string eventSql)
    {
        var model = await BuildModelAsync(Tables + eventSql);
        var provider = new MariaDbDatabaseProvider("Server=unused");

        return new MariaDbScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, model, new Model()));
    }

    [Fact]
    public async Task CreateEvent_ScriptsOneTimeSchedule()
    {
        var script = await ScriptAgainstEmptyAsync(
            "CREATE EVENT rollup ON SCHEDULE AT '2030-01-01 00:00:00' "
            + "DO INSERT INTO stats (n) VALUES (1);");

        Assert.Contains(
            "CREATE EVENT `rollup` ON SCHEDULE AT '2030-01-01 00:00:00' DO",
            script);
        // The body follows on its own line, emitted verbatim.
        Assert.Contains("INSERT INTO stats (n) VALUES (1)", script);
    }

    [Fact]
    public async Task CreateEvent_ScriptsRecurringSchedule()
    {
        var script = await ScriptAgainstEmptyAsync(
            "CREATE EVENT rollup ON SCHEDULE EVERY 2 HOUR STARTS '2030-01-01 00:00:00' "
            + "ENDS '2031-01-01 00:00:00' DO SELECT 1;");

        Assert.Contains(
            "ON SCHEDULE EVERY 2 HOUR STARTS '2030-01-01 00:00:00' ENDS '2031-01-01 00:00:00'",
            script);
    }

    [Fact]
    public async Task CreateEvent_ScriptsCompoundInterval()
    {
        // The model stores the catalog's space-separated form ('2 3'); the generator must
        // write it back as the colon-separated literal the CREATE syntax accepts.
        var script = await ScriptAgainstEmptyAsync(
            "CREATE EVENT rollup ON SCHEDULE EVERY '2:3' DAY_HOUR "
            + "STARTS '2030-01-01 00:00:00' DO SELECT 1;");

        Assert.Contains("EVERY '2:3' DAY_HOUR", script);
    }

    [Fact]
    public async Task CreateEvent_ScriptsPreserveStatusAndComment()
    {
        var script = await ScriptAgainstEmptyAsync(
            "CREATE EVENT rollup ON SCHEDULE AT '2030-01-01 00:00:00' "
            + "ON COMPLETION PRESERVE DISABLE COMMENT 'nightly' DO SELECT 1;");

        Assert.Contains("ON COMPLETION PRESERVE", script);
        Assert.Contains("DISABLE", script);
        Assert.Contains("COMMENT 'nightly'", script);
    }

    [Fact]
    public async Task CreateEvent_ScriptsDisableOnSlave()
    {
        var script = await ScriptAgainstEmptyAsync(
            "CREATE EVENT rollup ON SCHEDULE AT '2030-01-01 00:00:00' "
            + "DISABLE ON SLAVE DO SELECT 1;");

        Assert.Contains("DISABLE ON SLAVE", script);
    }

    [Fact]
    public async Task CreateEvent_OmitsDefaultedClauses()
    {
        var script = await ScriptAgainstEmptyAsync(
            "CREATE EVENT rollup ON SCHEDULE AT '2030-01-01 00:00:00' DO SELECT 1;");

        Assert.DoesNotContain("ON COMPLETION", script);
        Assert.DoesNotContain("DISABLE", script);
        Assert.DoesNotContain("COMMENT", script);
    }

    [Fact]
    public async Task CreateEvent_EscapesCommentQuotes()
    {
        var script = await ScriptAgainstEmptyAsync(
            "CREATE EVENT rollup ON SCHEDULE AT '2030-01-01 00:00:00' "
            + "COMMENT 'it''s nightly' DO SELECT 1;");

        Assert.Contains("COMMENT 'it''s nightly'", script);
    }

    [Fact]
    public async Task ChangedEvent_IsScriptedAsDropAndCreate()
    {
        // An event is replaceable: MariaDB has ALTER EVENT but MySQL's clause ordering and
        // partial-update semantics differ, so a changed event is scripted as DROP + CREATE,
        // matching how this provider treats views, routines and triggers.
        var source = await BuildModelAsync(
            Tables + "CREATE EVENT rollup ON SCHEDULE EVERY 2 DAY "
            + "STARTS '2030-01-01 00:00:00' DO SELECT 1;");
        var target = await BuildModelAsync(
            Tables + "CREATE EVENT rollup ON SCHEDULE EVERY 1 DAY "
            + "STARTS '2030-01-01 00:00:00' DO SELECT 1;");

        var provider = new MariaDbDatabaseProvider("Server=unused");
        var script = new MariaDbScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, source, target));

        Assert.Contains("DROP EVENT IF EXISTS `rollup`", script);
        Assert.Contains("CREATE EVENT `rollup`", script);
        Assert.Contains("EVERY 2 DAY", script);
    }

    [Fact]
    public async Task UnchangedEvent_ProducesNoScript()
    {
        const string sql = "CREATE EVENT rollup ON SCHEDULE EVERY 1 DAY "
            + "STARTS '2030-01-01 00:00:00' DO SELECT 1;";

        var source = await BuildModelAsync(Tables + sql);
        var target = await BuildModelAsync(Tables + sql);

        var provider = new MariaDbDatabaseProvider("Server=unused");
        var script = new MariaDbScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, source, target));

        Assert.DoesNotContain("EVENT", script);
    }

    [Fact]
    public async Task DroppedEvent_IsScriptedAsDrop()
    {
        var source = await BuildModelAsync(Tables);
        var target = await BuildModelAsync(
            Tables + "CREATE EVENT rollup ON SCHEDULE AT '2030-01-01 00:00:00' DO SELECT 1;");

        var provider = new MariaDbDatabaseProvider("Server=unused");
        var comparison = SchemaCompare.Compare(
            provider, source, target, new DeployOptions { DropObjectsNotInSource = true });
        var script = new MariaDbScriptGenerator().GenerateScript(comparison);

        Assert.Contains("DROP EVENT IF EXISTS `rollup`", script);
    }
}

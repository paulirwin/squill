using Squill.Core;
using Squill.TestFramework;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Fast, Docker-free tests over view script generation. Models are built with the
/// parser-based model builder and diffed against an empty target, so every view becomes a
/// CreateDelta.
/// </summary>
public class MariaDbViewScriptTests
{
    private const string Users =
        "CREATE TABLE users (id int PRIMARY KEY, name varchar(50), active tinyint(1));";

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
    public async Task CreateView_ScriptsItsDefinition()
    {
        var sql = await ScriptAgainstEmptyAsync($"""
            {Users}
            CREATE VIEW active_users AS SELECT id, name FROM users WHERE active = 1;
            """);

        Assert.Contains("CREATE VIEW `active_users`", sql);
        Assert.Contains("SELECT id, name FROM users WHERE active = 1", sql);
    }

    [Fact]
    public async Task CreateView_NamesItsColumns()
    {
        var sql = await ScriptAgainstEmptyAsync($"""
            {Users}
            CREATE VIEW v (a, b) AS SELECT id, name FROM users;
            """);

        Assert.Contains("CREATE VIEW `v` (`a`, `b`)", sql);
    }

    // CREATE OR REPLACE VIEW is MariaDB-only syntax; the generator targets MySQL too, so it
    // must never emit it.
    [Fact]
    public async Task CreateView_DoesNotUseOrReplace()
    {
        var sql = await ScriptAgainstEmptyAsync($"""
            {Users}
            CREATE OR REPLACE VIEW v AS SELECT id FROM users;
            """);

        Assert.DoesNotContain("OR REPLACE", sql);
    }

    [Fact]
    public async Task CreateView_IsScriptedAfterTables()
    {
        var sql = await ScriptAgainstEmptyAsync($"""
            CREATE VIEW v AS SELECT id FROM users;
            {Users}
            """);

        Assert.True(
            sql.IndexOf("CREATE TABLE", StringComparison.Ordinal)
            < sql.IndexOf("CREATE VIEW", StringComparison.Ordinal),
            $"Expected the table to be created before the view, but got:\n{sql}");
    }

    [Fact]
    public async Task UnchangedView_ProducesNoScript()
    {
        var model = await BuildModelAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id, name FROM users;
            """);

        var provider = new MariaDbDatabaseProvider("Server=unused");

        Assert.Empty(SchemaCompare.Compare(provider, model, model).Deltas);
    }

    [Fact]
    public async Task ChangedViewColumns_AreDroppedAndRecreated()
    {
        var before = await BuildModelAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users;
            """);

        var after = await BuildModelAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id, name FROM users;
            """);

        var provider = new MariaDbDatabaseProvider("Server=unused");

        var comparison = SchemaCompare.Compare(provider, after, before);

        var recreate = Assert.IsType<RecreateDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal(MariaDbElementTypes.SqlView, recreate.SourceElement.Type);

        var sql = new MariaDbScriptGenerator().GenerateScript(comparison);

        Assert.Contains("DROP VIEW IF EXISTS `v`;", sql);
        Assert.Contains("CREATE VIEW `v`", sql);
    }

    [Fact]
    public async Task ChangedViewQueryWithSameColumns_IsNotDetectedAgainstADatabase()
    {
        // A documented limitation (issue #42): both engines rewrite a view's query when
        // they store it, so a declared query can never be compared against an extracted
        // one. The target here is shaped like an extracted model — a view with no query.
        var after = await BuildModelAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users WHERE active = 1;
            """);

        var deployed = new Model();

        foreach (var element in after.Elements.Where(i => i.Type != MariaDbElementTypes.SqlView))
        {
            deployed.Elements.Add(element);
        }

        deployed.Elements.Add(MariaDbModelFactory.CreateView(
            SqlName.Object("v"), ["id"], definition: null));

        var provider = new MariaDbDatabaseProvider("Server=unused");

        Assert.Empty(SchemaCompare.Compare(provider, after, deployed).Deltas);
    }

    [Fact]
    public async Task DroppedView_IsDropped()
    {
        var before = await BuildModelAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users;
            """);

        var after = await BuildModelAsync(Users);

        var provider = new MariaDbDatabaseProvider("Server=unused");

        var comparison = SchemaCompare.Compare(
            provider, after, before, new DeployOptions { DropObjectsNotInSource = true });

        Assert.Contains(
            "DROP VIEW IF EXISTS `v`;",
            new MariaDbScriptGenerator().GenerateScript(comparison));
    }
}

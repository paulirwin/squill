using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Fast, Docker-free tests over view script generation. Models are built with the
/// parser-based model builder and diffed against an empty target, so every view becomes a
/// CreateDelta.
/// </summary>
public class PostgresViewScriptTests
{
    private const string Users =
        "CREATE TABLE users (id integer PRIMARY KEY, name varchar(50), active boolean);";

    private static async Task<Model> BuildModelAsync(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return (await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken)).Model;
    }

    private static async Task<string> ScriptAgainstEmptyAsync(string sql)
    {
        var model = await BuildModelAsync(sql);
        var provider = new PostgresDatabaseProvider("Host=unused");

        return new PostgresScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, model, new Model()));
    }

    [Fact]
    public async Task CreateView_ScriptsItsDefinition()
    {
        var sql = await ScriptAgainstEmptyAsync($"""
            {Users}
            CREATE VIEW active_users AS SELECT id, name FROM users WHERE active;
            """);

        Assert.Contains("CREATE OR REPLACE VIEW \"active_users\"", sql);
        Assert.Contains("SELECT id, name FROM users WHERE active", sql);
    }

    [Fact]
    public async Task CreateView_ScriptsItsExplicitColumnList()
    {
        var sql = await ScriptAgainstEmptyAsync($"""
            {Users}
            CREATE VIEW v (a, b) AS SELECT id, name FROM users;
            """);

        Assert.Contains("CREATE OR REPLACE VIEW \"v\" (\"a\", \"b\")", sql);
    }

    [Fact]
    public async Task CreateView_SchemaQualifiesNonPublicSchema()
    {
        var sql = await ScriptAgainstEmptyAsync($"""
            CREATE SCHEMA reporting;
            {Users}
            CREATE VIEW reporting.totals AS SELECT id FROM users;
            """);

        Assert.Contains("CREATE OR REPLACE VIEW \"reporting\".\"totals\"", sql);
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
            < sql.IndexOf("CREATE OR REPLACE VIEW", StringComparison.Ordinal),
            $"Expected the table to be created before the view, but got:\n{sql}");
    }

    [Fact]
    public async Task UnchangedView_ProducesNoScript()
    {
        var model = await BuildModelAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id, name FROM users;
            """);

        var provider = new PostgresDatabaseProvider("Host=unused");

        var comparison = SchemaCompare.Compare(provider, model, model);

        Assert.Empty(comparison.Deltas);
    }

    [Fact]
    public async Task ChangedViewColumns_AreRecreated()
    {
        var before = await BuildModelAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users;
            """);

        var after = await BuildModelAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id, name FROM users;
            """);

        var provider = new PostgresDatabaseProvider("Host=unused");

        var comparison = SchemaCompare.Compare(provider, after, before);

        var delta = Assert.Single(comparison.Deltas);
        var recreate = Assert.IsType<RecreateDelta>(delta);
        Assert.Equal(PostgresElementTypes.SqlView, recreate.SourceElement.Type);
    }

    [Fact]
    public async Task ChangedViewQueryWithSameColumns_IsNotDetectedAgainstADatabase()
    {
        // A documented limitation (issue #42): PostgreSQL rewrites a view's query when it
        // stores it, so a declared query can never be compared against an extracted one. A
        // view's identity is therefore its name and column list, and a WHERE-clause-only
        // change does not surface as a delta against a deployed database.
        //
        // The target here is shaped like an extracted model — a view element with no
        // definition, which is what PostgresDatabaseModelBuilder produces.
        var after = await BuildModelAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users WHERE NOT active;
            """);

        var deployed = new Model();

        foreach (var element in after.Elements.Where(i => i.Type != PostgresElementTypes.SqlView))
        {
            deployed.Elements.Add(element);
        }

        deployed.Elements.Add(PostgresModelFactory.CreateView(
            SqlName.Object("public", "v"), "public", ["id"], definition: null));

        var provider = new PostgresDatabaseProvider("Host=unused");

        Assert.Empty(SchemaCompare.Compare(provider, after, deployed).Deltas);
    }

    [Fact]
    public async Task ViewDefinition_IsNotComparedAgainstADatabase()
    {
        // The mirror of the above: an unchanged view must not be recreated on every deploy
        // just because the database reports a rewritten query. This is what makes a
        // redeploy of unchanged source a genuine no-op.
        var model = await BuildModelAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id, name FROM users;
            """);

        var deployed = new Model();

        foreach (var element in model.Elements.Where(i => i.Type != PostgresElementTypes.SqlView))
        {
            deployed.Elements.Add(element);
        }

        deployed.Elements.Add(PostgresModelFactory.CreateView(
            SqlName.Object("public", "v"), "public", ["id", "name"], definition: null));

        var provider = new PostgresDatabaseProvider("Host=unused");

        Assert.Empty(SchemaCompare.Compare(provider, model, deployed).Deltas);
    }

    [Fact]
    public async Task DroppedView_IsDropped()
    {
        var before = await BuildModelAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users;
            """);

        var after = await BuildModelAsync(Users);

        var provider = new PostgresDatabaseProvider("Host=unused");

        var comparison = SchemaCompare.Compare(
            provider, after, before, new DeployOptions { DropObjectsNotInSource = true });

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("DROP VIEW IF EXISTS \"v\";", sql);
    }
}

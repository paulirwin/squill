using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Model-builder and script-generator tests for <c>CREATE TRIGGER</c> (issue #83).
/// </summary>
public class CreateTriggerModelTests
{
    // A table and a trigger function the trigger references, so the model is self-consistent.
    private const string PreambleSql =
        "CREATE TABLE film (\n"
        + "    film_id integer PRIMARY KEY,\n"
        + "    last_update timestamp NOT NULL\n"
        + ");\n"
        + "CREATE FUNCTION last_updated() RETURNS trigger LANGUAGE plpgsql\n"
        + "    AS $$ BEGIN NEW.last_update = now(); RETURN NEW; END $$;\n";

    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            TestContext.Current.CancellationToken);

    private static async Task<string> ScriptAgainstEmptyAsync(string sql)
    {
        var model = await BuildModelAsync(sql);
        var provider = new PostgresDatabaseProvider("Host=unused");

        return new PostgresScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, model, new Model()));
    }

    [Fact]
    public async Task Trigger_IsModeledWithItsFacets()
    {
        var model = await BuildModelAsync(PreambleSql + """
            CREATE TRIGGER last_updated
                BEFORE UPDATE ON film
                FOR EACH ROW
                EXECUTE FUNCTION last_updated();
            """);

        var trigger = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlTrigger);

        Assert.Equal("public.film.last_updated", trigger.Name);
        Assert.Equal("public", PostgresModelFactory.GetSchema(trigger));
        Assert.Equal("last_updated", trigger.GetProperty<string>(PostgresPropertyNames.RoutineName));
        Assert.Equal("BEFORE", trigger.GetProperty<string>(PostgresPropertyNames.Timing));
        Assert.Equal("UPDATE", trigger.GetProperty<string>(PostgresPropertyNames.Events));
        Assert.Equal("ROW", trigger.GetProperty<string>(PostgresPropertyNames.Level));
        // A bare function name is stored bare (not force-qualified with the table's schema),
        // so a built-in or search-path function resolves correctly at deploy time.
        Assert.Equal("last_updated", trigger.GetProperty<string>(PostgresPropertyNames.TriggerFunction));
        Assert.Equal("", trigger.GetProperty<string>(PostgresPropertyNames.FunctionArguments));
    }

    [Fact]
    public async Task Trigger_OrredEventsAreRenderedCanonically()
    {
        var model = await BuildModelAsync(PreambleSql + """
            CREATE TRIGGER film_fulltext_trigger
                BEFORE UPDATE OR INSERT ON film
                FOR EACH ROW
                EXECUTE FUNCTION tsvector_update_trigger('fulltext', 'pg_catalog.english', 'title');
            """);

        var trigger = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlTrigger);

        // Events are rendered in a fixed order (INSERT before UPDATE) regardless of how they
        // were written, so the model is canonical and hash-matches an extracted one.
        Assert.Equal("INSERT OR UPDATE", trigger.GetProperty<string>(PostgresPropertyNames.Events));
        Assert.Equal("tsvector_update_trigger", trigger.GetProperty<string>(PostgresPropertyNames.TriggerFunction));
        Assert.Equal("fulltext, pg_catalog.english, title",
            trigger.GetProperty<string>(PostgresPropertyNames.FunctionArguments));
    }

    [Fact]
    public async Task Trigger_UnresolvedTableIsAnError()
    {
        // No CREATE TABLE for the referenced table, so the trigger is an unresolved reference.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => BuildModelAsync(
            "CREATE FUNCTION f() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;\n"
            + "CREATE TRIGGER t BEFORE UPDATE ON missing_table FOR EACH ROW EXECUTE FUNCTION f();"));

        Assert.Contains("missing_table", ex.ToString());
    }

    [Fact]
    public async Task Script_EmitsCreateTrigger()
    {
        var sql = await ScriptAgainstEmptyAsync(PreambleSql + """
            CREATE TRIGGER last_updated
                BEFORE UPDATE ON film
                FOR EACH ROW
                EXECUTE FUNCTION last_updated();
            """);

        Assert.Contains("CREATE TRIGGER \"last_updated\"", sql);
        Assert.Contains("BEFORE UPDATE ON \"film\"", sql);
        Assert.Contains("FOR EACH ROW", sql);
        Assert.Contains("EXECUTE FUNCTION \"last_updated\"();", sql);
    }

    [Fact]
    public async Task Script_EmitsTriggerFunctionArgumentsAsStringLiterals()
    {
        var sql = await ScriptAgainstEmptyAsync(PreambleSql + """
            CREATE TRIGGER film_fulltext_trigger
                BEFORE INSERT OR UPDATE ON film
                FOR EACH ROW
                EXECUTE FUNCTION tsvector_update_trigger('fulltext', 'pg_catalog.english', 'title');
            """);

        Assert.Contains(
            "EXECUTE FUNCTION \"tsvector_update_trigger\"('fulltext', 'pg_catalog.english', 'title');",
            sql);
    }

    [Fact]
    public async Task Script_TriggerIsCreatedAfterItsTableAndFunction()
    {
        var sql = await ScriptAgainstEmptyAsync(PreambleSql + """
            CREATE TRIGGER last_updated
                BEFORE UPDATE ON film
                FOR EACH ROW
                EXECUTE FUNCTION last_updated();
            """);

        var tableIndex = sql.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        var fnIndex = sql.IndexOf("CREATE OR REPLACE FUNCTION", StringComparison.Ordinal);
        var trigIndex = sql.IndexOf("CREATE TRIGGER", StringComparison.Ordinal);

        Assert.True(tableIndex >= 0 && fnIndex >= 0 && trigIndex >= 0);
        Assert.True(tableIndex < trigIndex, "trigger should be created after its table");
        Assert.True(fnIndex < trigIndex, "trigger should be created after its function");
    }

    [Fact]
    public async Task Script_DropTriggerNamesTheTable()
    {
        var model = await BuildModelAsync(PreambleSql + """
            CREATE TRIGGER last_updated
                BEFORE UPDATE ON film
                FOR EACH ROW
                EXECUTE FUNCTION last_updated();
            """);
        var provider = new PostgresDatabaseProvider("Host=unused");

        var options = new DeployOptions { DropObjectsNotInSource = true, BlockOnPossibleDataLoss = false };
        var sql = new PostgresScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, new Model(), model, options));

        Assert.Contains("DROP TRIGGER IF EXISTS \"last_updated\" ON \"film\";", sql);
    }
}

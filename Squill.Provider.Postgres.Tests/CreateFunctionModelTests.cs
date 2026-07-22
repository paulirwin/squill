using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Model-builder and script-generator tests for <c>CREATE FUNCTION</c> (issue #81).
/// </summary>
public class CreateFunctionModelTests
{
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
    public async Task Function_IsModeledWithReturnTypeAndBody()
    {
        var model = await BuildModelAsync(
            "CREATE FUNCTION add_one(n integer) RETURNS integer LANGUAGE sql AS $$ SELECT n + 1 $$;");

        var fn = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlFunction);

        Assert.Equal("public.add_one(integer)", fn.Name);
        Assert.Equal("public", PostgresModelFactory.GetSchema(fn));
        Assert.Equal("add_one", fn.GetProperty<string>(PostgresPropertyNames.RoutineName));
        Assert.Equal("integer", fn.GetProperty<string>(PostgresPropertyNames.ArgumentTypes));
        Assert.Equal("integer", fn.GetProperty<string>(PostgresPropertyNames.ReturnType));
        Assert.Equal("sql", fn.GetProperty<string>(PostgresPropertyNames.Language));
        Assert.Contains("SELECT n + 1", fn.GetProperty<string>(PostgresPropertyNames.Body));
    }

    [Fact]
    public async Task SetReturning_IsModeled()
    {
        var model = await BuildModelAsync(
            "CREATE FUNCTION ids() RETURNS SETOF integer LANGUAGE sql AS $$ SELECT 1 $$;");

        var fn = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlFunction);
        Assert.True(fn.GetProperty<bool?>(PostgresPropertyNames.ReturnsSet));
    }

    [Fact]
    public async Task Overloads_AreDistinctElements()
    {
        var model = await BuildModelAsync("""
            CREATE FUNCTION f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;
            CREATE FUNCTION f(a text) RETURNS text LANGUAGE sql AS $$ SELECT a $$;
            """);

        var functions = model.Elements.Where(i => i.Type == PostgresElementTypes.SqlFunction).ToList();
        Assert.Equal(2, functions.Count);
        Assert.Contains(functions, f => f.Name as string == "public.f(integer)");
        Assert.Contains(functions, f => f.Name as string == "public.f(text)");
    }

    [Fact]
    public async Task VolatilityAndStrict_StoredOnlyWhenNonDefault()
    {
        var model = await BuildModelAsync(
            "CREATE FUNCTION f() RETURNS integer LANGUAGE sql IMMUTABLE STRICT AS $$ SELECT 1 $$;");

        var fn = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlFunction);
        Assert.Equal("IMMUTABLE", fn.GetProperty<string>(PostgresPropertyNames.Volatility));
        Assert.True(fn.GetProperty<bool?>(PostgresPropertyNames.IsStrict));

        // A plain VOLATILE / CALLED ON NULL INPUT function stores neither (they are defaults).
        var model2 = await BuildModelAsync(
            "CREATE FUNCTION g() RETURNS integer LANGUAGE sql VOLATILE AS $$ SELECT 1 $$;");
        var fn2 = Assert.Single(model2.Elements, i => i.Type == PostgresElementTypes.SqlFunction);
        Assert.Null(fn2.GetProperty<string>(PostgresPropertyNames.Volatility));
        Assert.Null(fn2.GetProperty<bool?>(PostgresPropertyNames.IsStrict));
    }

    [Fact]
    public async Task Script_EmitsCreateOrReplaceFunction()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE FUNCTION last_day(t timestamp) RETURNS date LANGUAGE sql IMMUTABLE STRICT
            AS $$ SELECT (date_trunc('month', t))::date $$;
            """);

        Assert.Contains("CREATE OR REPLACE FUNCTION \"last_day\"(IN t timestamp without time zone)", sql);
        Assert.Contains("RETURNS date", sql);
        Assert.Contains("LANGUAGE \"sql\"", sql);
        Assert.Contains("IMMUTABLE", sql);
        Assert.Contains("STRICT", sql);
    }

    [Fact]
    public async Task Script_EmitsSetofReturn()
    {
        var sql = await ScriptAgainstEmptyAsync(
            "CREATE FUNCTION ids() RETURNS SETOF integer LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.Contains("RETURNS SETOF integer", sql);
    }

    [Fact]
    public async Task Script_FunctionIsCreatedAfterTableItReferences()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE TABLE widgets (id integer PRIMARY KEY);
            CREATE FUNCTION widget_count() RETURNS bigint LANGUAGE sql
            AS $$ SELECT count(*) FROM widgets $$;
            """);

        var tableIndex = sql.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        var fnIndex = sql.IndexOf("CREATE OR REPLACE FUNCTION", StringComparison.Ordinal);

        Assert.True(tableIndex >= 0 && fnIndex >= 0);
        Assert.True(tableIndex < fnIndex, "function should be created after the table it references");
    }

    [Fact]
    public async Task DuplicateFunction_IsABuildError()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => BuildModelAsync("""
            CREATE FUNCTION f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;
            CREATE FUNCTION f(a integer) RETURNS integer LANGUAGE sql AS $$ SELECT a + 1 $$;
            """));
    }
}

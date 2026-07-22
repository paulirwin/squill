using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Model-builder and script-generator tests for <c>CREATE AGGREGATE</c> (issue #82).
/// </summary>
public class CreateAggregateModelTests
{
    // A state function the aggregate references, so the model is self-consistent.
    private const string StateFunctionSql =
        "CREATE FUNCTION _group_concat(text, text) RETURNS text LANGUAGE sql "
        + "AS $$ SELECT $1 || $2 $$;\n";

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
    public async Task Aggregate_IsModeledWithStateFunctionAndType()
    {
        var model = await BuildModelAsync(StateFunctionSql + """
            CREATE AGGREGATE group_concat(text) (
                SFUNC = _group_concat,
                STYPE = text
            );
            """);

        var agg = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlAggregate);

        Assert.Equal("public.group_concat(text)", agg.Name);
        Assert.Equal("public", PostgresModelFactory.GetSchema(agg));
        Assert.Equal("group_concat", agg.GetProperty<string>(PostgresPropertyNames.RoutineName));
        Assert.Equal("text", agg.GetProperty<string>(PostgresPropertyNames.ArgumentTypes));
        Assert.Equal("public._group_concat", agg.GetProperty<string>(PostgresPropertyNames.StateFunction));
        Assert.Equal("text", agg.GetProperty<string>(PostgresPropertyNames.StateType));
    }

    [Fact]
    public async Task Overloads_AreDistinctElements()
    {
        var model = await BuildModelAsync("""
            CREATE FUNCTION accum(text, integer) RETURNS text LANGUAGE sql AS $$ SELECT $1 $$;
            CREATE FUNCTION accum(text, text) RETURNS text LANGUAGE sql AS $$ SELECT $1 $$;
            CREATE AGGREGATE my_agg(integer) (SFUNC = accum, STYPE = text);
            CREATE AGGREGATE my_agg(text) (SFUNC = accum, STYPE = text);
            """);

        var aggregates = model.Elements.Where(i => i.Type == PostgresElementTypes.SqlAggregate).ToList();
        Assert.Equal(2, aggregates.Count);
        Assert.Contains(aggregates, a => a.Name as string == "public.my_agg(integer)");
        Assert.Contains(aggregates, a => a.Name as string == "public.my_agg(text)");
    }

    [Fact]
    public async Task Script_EmitsCreateAggregate()
    {
        var sql = await ScriptAgainstEmptyAsync(StateFunctionSql + """
            CREATE AGGREGATE group_concat(text) (
                SFUNC = _group_concat,
                STYPE = text
            );
            """);

        Assert.Contains("CREATE AGGREGATE \"group_concat\"(text) (", sql);
        Assert.Contains("SFUNC = \"public\".\"_group_concat\"", sql);
        Assert.Contains("STYPE = text", sql);
    }

    [Fact]
    public async Task Script_AggregateIsCreatedAfterItsStateFunction()
    {
        var sql = await ScriptAgainstEmptyAsync(StateFunctionSql + """
            CREATE AGGREGATE group_concat(text) (
                SFUNC = _group_concat,
                STYPE = text
            );
            """);

        var fnIndex = sql.IndexOf("CREATE OR REPLACE FUNCTION", StringComparison.Ordinal);
        var aggIndex = sql.IndexOf("CREATE AGGREGATE", StringComparison.Ordinal);

        Assert.True(fnIndex >= 0 && aggIndex >= 0);
        Assert.True(fnIndex < aggIndex, "aggregate should be created after its state function");
    }

    [Fact]
    public async Task Script_DropAggregateUsesSignature()
    {
        var model = await BuildModelAsync(StateFunctionSql + """
            CREATE AGGREGATE group_concat(text) (
                SFUNC = _group_concat,
                STYPE = text
            );
            """);
        var provider = new PostgresDatabaseProvider("Host=unused");

        // Compare an empty desired model against the built one so the aggregate is dropped;
        // dropping objects not in source must be opted in.
        var options = new DeployOptions { DropObjectsNotInSource = true, BlockOnPossibleDataLoss = false };
        var sql = new PostgresScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, new Model(), model, options));

        Assert.Contains("DROP AGGREGATE IF EXISTS \"group_concat\"(text);", sql);
    }
}

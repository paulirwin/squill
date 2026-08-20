using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Model-builder and script-generator tests for the <c>CREATE FUNCTION</c> declaration forms
/// added by issue #213.
///
/// The facets modeled here were each measured against postgres:18.4 rather than inferred
/// from the grammar, because what PostgreSQL stores is what a redeploy compares against:
/// <list type="bullet">
/// <item><c>RETURNS TABLE</c> is stored as TABLE-mode arguments with <c>proretset</c> set,
/// which is exactly the shape the database model builder already reads back.</item>
/// <item>A <c>SET</c> clause is stored in <c>proconfig</c> as <c>name=value</c>, and
/// <c>SET x = a, b</c> and <c>SET x TO 'a', 'b'</c> store the identical entry — but passing
/// the list as one quoted string does not, which is why the values are re-emitted
/// individually quoted.</item>
/// <item><c>FROM CURRENT</c>, <c>RESET</c>, <c>%TYPE</c>, <c>WINDOW</c>, <c>TRANSFORM</c>
/// and a linked C body provably cannot round-trip, so they warn rather than being
/// modeled.</item>
/// </list>
/// </summary>
public class PostgresFunctionDeclarationFormTests
{
    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            TestContext.Current.CancellationToken);

    private static async Task<IReadOnlyList<SqlSourceDiagnostic>> WarningsAsync(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        var result = await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        return result.Warnings;
    }

    private static async Task<string> ScriptAgainstEmptyAsync(string sql)
    {
        var model = await BuildModelAsync(sql);
        var provider = new PostgresDatabaseProvider("Host=unused");

        return new PostgresScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, model, new Model()));
    }

    private static Element SingleFunction(Model model)
        => Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlFunction);

    [Fact]
    public async Task ReturnsTable_IsModeledAsTableModeArguments()
    {
        var model = await BuildModelAsync("""
            CREATE FUNCTION report(p integer) RETURNS TABLE (a integer, b text)
            LANGUAGE sql AS $$ SELECT 1, 'x' $$;
            """);

        var fn = SingleFunction(model);

        // Only the IN parameter forms the identity signature, matching pg_proc.proargtypes.
        Assert.Equal("public.report(integer)", fn.Name);
        Assert.Equal("integer", fn.GetProperty<string>(PostgresPropertyNames.ArgumentTypes));

        Assert.Equal(
            "IN p integer, TABLE a integer, TABLE b text",
            fn.GetProperty<string>(PostgresPropertyNames.Arguments));

        Assert.Equal("record", fn.GetProperty<string>(PostgresPropertyNames.ReturnType));
        Assert.True(fn.GetProperty<bool?>(PostgresPropertyNames.ReturnsSet));
    }

    [Fact]
    public async Task Script_EmitsReturnsTableColumnsAsTableArguments()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE FUNCTION report() RETURNS TABLE (a integer, b text)
            LANGUAGE sql AS $$ SELECT 1, 'x' $$;
            """);

        // The columns go back into a RETURNS TABLE clause rather than the argument list.
        // Measured on postgres:18.4: `TABLE a integer` is a syntax error in an argument
        // list, and re-declaring the columns as OUT parameters stores argument mode 'o'
        // rather than the 't' a RETURNS TABLE function has, so it would re-diff forever.
        Assert.Contains("CREATE OR REPLACE FUNCTION \"report\"()", sql);
        Assert.Contains("RETURNS TABLE (a integer, b text)", sql);
    }

    [Fact]
    public async Task OutOnlyFunction_IsModeledWithTheDerivedReturnType()
    {
        var model = await BuildModelAsync(
            "CREATE FUNCTION f(OUT a integer) LANGUAGE sql AS $$ SELECT 1 $$;");

        var fn = SingleFunction(model);

        // An OUT parameter is not part of the identity signature.
        Assert.Equal("public.f()", fn.Name);
        Assert.Equal("integer", fn.GetProperty<string>(PostgresPropertyNames.ReturnType));
        Assert.Equal("OUT a integer", fn.GetProperty<string>(PostgresPropertyNames.Arguments));
    }

    [Fact]
    public async Task SetConfiguration_IsModeled()
    {
        var model = await BuildModelAsync("""
            CREATE FUNCTION f() RETURNS integer LANGUAGE sql SECURITY DEFINER
            SET search_path = pg_catalog, pg_temp
            AS $$ SELECT 1 $$;
            """);

        var fn = SingleFunction(model);

        // Stored the way pg_proc.proconfig stores it, so an extracted model matches.
        Assert.Equal(
            "search_path=pg_catalog, pg_temp",
            fn.GetProperty<string>(PostgresPropertyNames.Configuration));
    }

    [Fact]
    public async Task Script_EmitsSetConfigurationWithIndividuallyQuotedValues()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE FUNCTION f() RETURNS integer LANGUAGE sql SECURITY DEFINER
            SET search_path = pg_catalog, pg_temp
            AS $$ SELECT 1 $$;
            """);

        // Measured: this spelling stores search_path=pg_catalog, pg_temp, whereas quoting
        // the list as one string stores a different, single-element value.
        //
        // The GUC name is quoted because PostgreSQL canonicalizes some names to a mixed-case
        // spelling (measured: `timezone` is stored as `TimeZone`) that an unquoted identifier
        // would fold back to lower case. Measured: quoting a lower-case name changes nothing.
        Assert.Contains("SET \"search_path\" TO 'pg_catalog', 'pg_temp'", sql);
        Assert.Contains("SECURITY DEFINER", sql);
    }

    [Fact]
    public async Task SetConfiguration_MultipleClausesArePreservedInOrder()
    {
        var model = await BuildModelAsync("""
            CREATE FUNCTION f() RETURNS integer LANGUAGE sql
            SET enable_seqscan = off SET work_mem = '32MB'
            AS $$ SELECT 1 $$;
            """);

        // proconfig is an array in declaration order; the model joins the entries with the
        // same record separator the database model builder uses when it reads them back,
        // since it cannot occur inside a GUC name or value.
        var configuration = SingleFunction(model)
            .GetProperty<string>(PostgresPropertyNames.Configuration);

        Assert.Equal(
            string.Join('\u001e', "enable_seqscan=off", "work_mem=32MB"),
            configuration);
    }

    [Fact]
    public async Task NoSetConfiguration_LeavesThePropertyAbsent()
    {
        // An absent property keeps a plain function's element shape identical to the
        // extracted one, so it does not re-diff.
        var model = await BuildModelAsync(
            "CREATE FUNCTION f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.Null(SingleFunction(model).GetProperty<string>(PostgresPropertyNames.Configuration));
    }

    [Fact]
    public async Task ProcedureSetConfiguration_IsModeled()
    {
        var model = await BuildModelAsync(
            "CREATE PROCEDURE p() LANGUAGE sql SET search_path = public AS $$ SELECT 1 $$;");

        var proc = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlProcedure);

        Assert.Equal(
            "search_path=public",
            proc.GetProperty<string>(PostgresPropertyNames.Configuration));
    }

    [Fact]
    public async Task SetFromCurrent_WarnsAndIsNotModeled()
    {
        // FROM CURRENT captures the deploying session's value, so what lands in the database
        // depends on who ran the deploy and could never be compared back.
        var sql = "CREATE FUNCTION f() RETURNS integer LANGUAGE sql "
            + "SET search_path FROM CURRENT AS $$ SELECT 1 $$;";

        var warning = Assert.Single(await WarningsAsync(sql));

        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("FROM CURRENT", warning.Message);

        Assert.Null(SingleFunction(await BuildModelAsync(sql))
            .GetProperty<string>(PostgresPropertyNames.Configuration));
    }

    [Fact]
    public async Task Reset_WarnsAndIsNotModeled()
    {
        // Measured: a RESET on a declaration leaves proconfig null, which is indistinguishable
        // from having written no clause, so there is nothing to model.
        var sql = "CREATE FUNCTION f() RETURNS integer LANGUAGE sql "
            + "RESET search_path AS $$ SELECT 1 $$;";

        var warning = Assert.Single(await WarningsAsync(sql));

        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("RESET", warning.Message);
    }

    [Fact]
    public async Task PlannerHints_WarnAndAreNotModeled()
    {
        var warnings = await WarningsAsync("""
            CREATE FUNCTION f() RETURNS SETOF integer LANGUAGE sql
            COST 5 ROWS 100 PARALLEL SAFE LEAKPROOF AS $$ SELECT 1 $$;
            """);

        // COST, ROWS and PARALLEL change how the planner may use the function, so dropping
        // them silently would change behaviour without telling the author.
        var message = Assert.Single(warnings).Message;

        Assert.Equal("SQ1002", warnings[0].Code);
        Assert.Contains("COST", message);
        Assert.Contains("ROWS", message);
        Assert.Contains("PARALLEL", message);
        Assert.Contains("LEAKPROOF", message);
    }

    [Fact]
    public async Task PlainFunction_WarnsAboutNothing()
    {
        Assert.Empty(await WarningsAsync(
            "CREATE FUNCTION f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;"));
    }

    [Fact]
    public async Task WindowFunction_WarnsAndIsNotModeled()
    {
        var sql = "CREATE FUNCTION f() RETURNS integer LANGUAGE internal WINDOW AS 'window_rank';";

        var warning = Assert.Single(await WarningsAsync(sql));

        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("WINDOW", warning.Message);

        // The whole function is unmodeled: its implementation lives in a linked library that
        // Squill cannot reproduce, so deploying a partial version of it would be wrong.
        Assert.DoesNotContain(
            (await BuildModelAsync(sql)).Elements,
            i => i.Type == PostgresElementTypes.SqlFunction);
    }

    [Fact]
    public async Task LinkedCFunction_WarnsAndIsNotModeled()
    {
        var sql = "CREATE FUNCTION f() RETURNS integer LANGUAGE c AS 'obj', 'sym';";

        var warning = Assert.Single(await WarningsAsync(sql));

        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("link_symbol", warning.Message);

        Assert.DoesNotContain(
            (await BuildModelAsync(sql)).Elements,
            i => i.Type == PostgresElementTypes.SqlFunction);
    }

    [Fact]
    public async Task Transform_WarnsAndIsNotModeled()
    {
        var sql = "CREATE FUNCTION f(x integer) RETURNS integer TRANSFORM FOR TYPE hstore "
            + "LANGUAGE sql AS $$ SELECT 1 $$;";

        var warning = Assert.Single(await WarningsAsync(sql));

        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("TRANSFORM", warning.Message);

        Assert.DoesNotContain(
            (await BuildModelAsync(sql)).Elements,
            i => i.Type == PostgresElementTypes.SqlFunction);
    }

    [Fact]
    public async Task SupportFunction_Warns()
    {
        var warning = Assert.Single(await WarningsAsync(
            "CREATE FUNCTION f(integer) RETURNS integer LANGUAGE sql SUPPORT s AS $$ SELECT 1 $$;"));

        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("SUPPORT", warning.Message);
    }

    [Fact]
    public async Task SetConfiguration_ChangesTheFunctionHash()
    {
        // A configuration clause is part of the function's identity: adding one to an
        // existing function must be seen as a change, not a no-op.
        var without = await BuildModelAsync(
            "CREATE FUNCTION f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;");

        var with = await BuildModelAsync("""
            CREATE FUNCTION f() RETURNS integer LANGUAGE sql
            SET search_path = pg_catalog AS $$ SELECT 1 $$;
            """);

        Assert.False(HashUtility.HashesEqual(without.Hash, with.Hash));
    }
}

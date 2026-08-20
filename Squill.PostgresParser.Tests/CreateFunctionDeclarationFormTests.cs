using Squill.PostgresParser.Syntax;
using Squill.TestFramework;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// Parser tests for the <c>CREATE FUNCTION</c> declaration forms that used to throw
/// (issue #213): <c>RETURNS TABLE</c>, an OUT-parameter-only function with no RETURNS,
/// <c>SET</c>/<c>RESET</c> configuration clauses, and the planner-hint attributes
/// (<c>COST</c>, <c>ROWS</c>, <c>PARALLEL</c>, <c>LEAKPROOF</c>, <c>SUPPORT</c>).
///
/// The forms that provably cannot round-trip stay rejected, and are asserted here so the
/// rejection is deliberate rather than incidental: <c>%TYPE</c> is resolved against the
/// catalog when the function is created (measured: <c>t.c%TYPE</c> comes back as plain
/// <c>integer</c>), a linked C function has no body to model, and <c>WINDOW</c> and
/// <c>TRANSFORM</c> describe an implementation Squill cannot reproduce.
/// </summary>
public class CreateFunctionDeclarationFormTests
{
    private static CreateFunctionStatement ParseOne(string text)
        => ParseAssertions.Single<CreateFunctionStatement>(new AntlrPostgresParser().Parse(text).Statements);

    [Fact]
    public void ReturnsTable_BecomesTableModeParameters()
    {
        var stmt = ParseOne("""
            CREATE FUNCTION f(p integer) RETURNS TABLE (a integer, b text)
            LANGUAGE sql AS $$ SELECT 1, 'x' $$;
            """);

        // Measured on postgres:18.4: RETURNS TABLE is stored as TABLE-mode arguments
        // (proargmodes = {i,t,t}) with proretset set, exactly as a SETOF function is.
        Assert.True(stmt.ReturnsSet);
        Assert.Equal(3, stmt.Parameters.Count);

        Assert.Equal(ParameterMode.In, stmt.Parameters[0].Mode);
        Assert.Equal("p", stmt.Parameters[0].Name?.Name);

        Assert.Equal(ParameterMode.Table, stmt.Parameters[1].Mode);
        Assert.Equal("a", stmt.Parameters[1].Name?.Name);
        Assert.Equal("integer", stmt.Parameters[1].DataType.TypeName);

        Assert.Equal(ParameterMode.Table, stmt.Parameters[2].Mode);
        Assert.Equal("b", stmt.Parameters[2].Name?.Name);
        Assert.Equal("text", stmt.Parameters[2].DataType.TypeName);
    }

    [Fact]
    public void ReturnsTable_SingleColumnReturnsThatColumnType()
    {
        // Measured: a one-column RETURNS TABLE reports prorettype as that column's type
        // rather than `record`, so the return type is the column's, not a composite.
        var stmt = ParseOne(
            "CREATE FUNCTION f() RETURNS TABLE (a integer) LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.True(stmt.ReturnsSet);
        Assert.Equal("integer", stmt.ReturnType!.TypeName);
    }

    [Fact]
    public void ReturnsTable_MultipleColumnsReturnRecord()
    {
        var stmt = ParseOne(
            "CREATE FUNCTION f() RETURNS TABLE (a integer, b text) LANGUAGE sql AS $$ SELECT 1, 'x' $$;");

        Assert.Equal("record", stmt.ReturnType!.TypeName);
    }

    [Fact]
    public void OutParametersOnly_ReturnTypeIsInferred()
    {
        // With no RETURNS clause the OUT parameters define the result. Measured: a single
        // OUT parameter reports prorettype as that parameter's type.
        var stmt = ParseOne("CREATE FUNCTION f(OUT a integer) LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.Equal("integer", stmt.ReturnType!.TypeName);
        Assert.False(stmt.ReturnsSet);
        Assert.Equal(ParameterMode.Out, stmt.Parameters[0].Mode);
    }

    [Fact]
    public void MultipleOutParametersOnly_ReturnRecord()
    {
        // Measured: two OUT parameters report prorettype as `record`.
        var stmt = ParseOne(
            "CREATE FUNCTION f(OUT a integer, OUT b text) LANGUAGE sql AS $$ SELECT 1, 'x' $$;");

        Assert.Equal("record", stmt.ReturnType!.TypeName);
    }

    [Fact]
    public void SetConfiguration_IsCapturedInDeclarationOrder()
    {
        var stmt = ParseOne("""
            CREATE FUNCTION f() RETURNS integer LANGUAGE sql SECURITY DEFINER
            SET search_path = pg_catalog, pg_temp
            SET work_mem = '64MB'
            AS $$ SELECT 1 $$;
            """);

        Assert.True(stmt.SecurityDefiner);
        Assert.Equal(2, stmt.Settings.Count);

        Assert.Equal("search_path", stmt.Settings[0].Name);
        Assert.Equal(["pg_catalog", "pg_temp"], stmt.Settings[0].Values);

        Assert.Equal("work_mem", stmt.Settings[1].Name);
        Assert.Equal(["64MB"], stmt.Settings[1].Values);
    }

    [Fact]
    public void SetConfiguration_ToSpellingIsTheSameAsEquals()
    {
        // Measured: `SET x = a, b` and `SET x TO 'a', 'b'` both store the identical
        // proconfig entry, so the two spellings must parse to the same settings.
        var equals = ParseOne(
            "CREATE FUNCTION f() RETURNS integer LANGUAGE sql SET search_path = pg_catalog, pg_temp AS $$ SELECT 1 $$;");
        var to = ParseOne(
            "CREATE FUNCTION f() RETURNS integer LANGUAGE sql SET search_path TO 'pg_catalog', 'pg_temp' AS $$ SELECT 1 $$;");

        Assert.Equal(equals.Settings[0].Name, to.Settings[0].Name);
        Assert.Equal(equals.Settings[0].Values, to.Settings[0].Values);
    }

    [Fact]
    public void SetConfiguration_FromCurrentIsMarked()
    {
        // FROM CURRENT resolves against the creating session, so it cannot round-trip; the
        // parser records it so the model builder can warn rather than silently drop it.
        var stmt = ParseOne(
            "CREATE FUNCTION f() RETURNS integer LANGUAGE sql SET search_path FROM CURRENT AS $$ SELECT 1 $$;");

        var setting = Assert.Single(stmt.Settings);
        Assert.Equal("search_path", setting.Name);
        Assert.True(setting.FromCurrent);
        Assert.Empty(setting.Values);
    }

    [Fact]
    public void ResetConfiguration_IsMarkedAsReset()
    {
        var stmt = ParseOne(
            "CREATE FUNCTION f() RETURNS integer LANGUAGE sql RESET search_path AS $$ SELECT 1 $$;");

        var setting = Assert.Single(stmt.Settings);
        Assert.Equal("search_path", setting.Name);
        Assert.True(setting.IsReset);
    }

    [Fact]
    public void ResetAll_IsMarkedAsResetOfAll()
    {
        var stmt = ParseOne(
            "CREATE FUNCTION f() RETURNS integer LANGUAGE sql RESET ALL AS $$ SELECT 1 $$;");

        var setting = Assert.Single(stmt.Settings);
        Assert.True(setting.IsReset);
        Assert.True(setting.IsAll);
    }

    [Fact]
    public void PlannerHints_AreCaptured()
    {
        var stmt = ParseOne("""
            CREATE FUNCTION f() RETURNS SETOF integer LANGUAGE sql
            COST 5 ROWS 100 PARALLEL SAFE LEAKPROOF AS $$ SELECT 1 $$;
            """);

        Assert.Equal("5", stmt.Cost);
        Assert.Equal("100", stmt.Rows);
        Assert.Equal("SAFE", stmt.Parallel);
        Assert.True(stmt.Leakproof);
    }

    [Fact]
    public void NotLeakproofIsExplicitlyFalse()
    {
        var stmt = ParseOne(
            "CREATE FUNCTION f() RETURNS integer LANGUAGE sql NOT LEAKPROOF AS $$ SELECT 1 $$;");

        Assert.False(stmt.Leakproof);
    }

    [Fact]
    public void Support_IsCaptured()
    {
        var stmt = ParseOne(
            "CREATE FUNCTION f(integer) RETURNS integer LANGUAGE sql SUPPORT my_support AS $$ SELECT 1 $$;");

        Assert.Equal("my_support", stmt.SupportFunction);
    }

    [Fact]
    public void WindowFunctionIsCaptured()
    {
        var stmt = ParseOne(
            "CREATE FUNCTION f() RETURNS integer LANGUAGE internal WINDOW AS 'window_rank';");

        Assert.True(stmt.IsWindow);
    }

    [Fact]
    public void TransformForTypeIsCaptured()
    {
        var stmt = ParseOne("""
            CREATE FUNCTION f(x integer) RETURNS integer TRANSFORM FOR TYPE hstore
            LANGUAGE sql AS $$ SELECT 1 $$;
            """);

        Assert.Equal(["hstore"], stmt.TransformTypes);
    }

    [Fact]
    public void LinkedCFunctionCarriesObjectAndSymbol()
    {
        var stmt = ParseOne("CREATE FUNCTION f() RETURNS integer LANGUAGE c AS 'obj', 'sym';");

        Assert.Equal("obj", stmt.Body);
        Assert.Equal("sym", stmt.LinkSymbol);
    }

    [Fact]
    public void PercentTypeReturnIsRejectedWithAnAccurateMessage()
    {
        // %TYPE is resolved against the catalog at creation time, so the declared spelling
        // is not what the database stores and could never be compared back.
        var ex = Assert.Throws<NotImplementedException>(() => ParseOne(
            "CREATE FUNCTION f() RETURNS t.c%TYPE LANGUAGE sql AS $$ SELECT 1 $$;"));

        Assert.Contains("%TYPE", ex.Message);
    }

    [Fact]
    public void PercentTypeParameterIsRejectedWithAnAccurateMessage()
    {
        var ex = Assert.Throws<NotImplementedException>(() => ParseOne(
            "CREATE FUNCTION f(x t.c%TYPE) RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;"));

        Assert.Contains("%TYPE", ex.Message);
    }

    [Fact]
    public void ProcedureSetConfigurationIsCaptured()
    {
        var stmt = ParseAssertions.Single<CreateProcedureStatement>(
            new AntlrPostgresParser().Parse(
                "CREATE PROCEDURE p() LANGUAGE sql SET search_path = public AS $$ SELECT 1 $$;")
                .Statements);

        var setting = Assert.Single(stmt.Settings);
        Assert.Equal("search_path", setting.Name);
        Assert.Equal(["public"], setting.Values);
    }
}

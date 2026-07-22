using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// Parser tests for <c>CREATE FUNCTION</c> (issue #81). Functions share the
/// <c>createfunctionstmt</c> grammar rule with procedures but add a RETURNS clause and
/// volatility/strictness attributes.
/// </summary>
public class CreateFunctionTests
{
    private static CreateFunctionStatement ParseOne(string text)
    {
        var root = new AntlrPostgresParser().Parse(text);
        return Assert.IsType<CreateFunctionStatement>(Assert.Single(root.Statements));
    }

    [Fact]
    public void SimpleSqlFunction()
    {
        var stmt = ParseOne("CREATE FUNCTION f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.Equal("f", stmt.Name.Segments[^1].Name);
        Assert.False(stmt.OrReplace);
        Assert.Empty(stmt.Parameters);
        Assert.NotNull(stmt.ReturnType);
        Assert.Equal("integer", stmt.ReturnType!.TypeName);
        Assert.False(stmt.ReturnsSet);
        Assert.Equal("sql", stmt.Language);
        Assert.Equal(" SELECT 1 ", stmt.Body);
    }

    [Fact]
    public void ReturnsSetof()
    {
        var stmt = ParseOne(
            "CREATE FUNCTION films() RETURNS SETOF integer LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.Equal("integer", stmt.ReturnType!.TypeName);
        Assert.True(stmt.ReturnsSet);
    }

    [Fact]
    public void ParametersWithModes()
    {
        var stmt = ParseOne("""
            CREATE FUNCTION film_in_stock(p_film_id integer, p_store_id integer, OUT p_film_count integer)
            RETURNS SETOF integer LANGUAGE sql AS $$ SELECT 1 $$;
            """);

        Assert.Equal(3, stmt.Parameters.Count);
        Assert.Equal("p_film_id", stmt.Parameters[0].Name?.Name);
        Assert.Equal(ParameterMode.In, stmt.Parameters[0].Mode);
        Assert.Equal(ParameterMode.Out, stmt.Parameters[2].Mode);
        Assert.Equal("p_film_count", stmt.Parameters[2].Name?.Name);
    }

    [Fact]
    public void PlpgsqlFunctionBodyIsVerbatim()
    {
        var stmt = ParseOne("""
            CREATE FUNCTION last_updated() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                NEW.last_update = now();
                RETURN NEW;
            END
            $$;
            """);

        Assert.Equal("plpgsql", stmt.Language);
        Assert.Equal("trigger", stmt.ReturnType!.TypeName);
        Assert.Contains("NEW.last_update = now();", stmt.Body);
    }

    [Fact]
    public void ImmutableStrictVolatility()
    {
        var stmt = ParseOne("""
            CREATE FUNCTION last_day(timestamp) RETURNS date LANGUAGE sql IMMUTABLE STRICT
            AS $$ SELECT $1::date $$;
            """);

        Assert.Equal(FunctionVolatility.Immutable, stmt.Volatility);
        Assert.True(stmt.Strict);
    }

    [Fact]
    public void StableAndCalledOnNullInput()
    {
        var stmt = ParseOne("""
            CREATE FUNCTION f() RETURNS integer LANGUAGE sql STABLE CALLED ON NULL INPUT
            AS $$ SELECT 1 $$;
            """);

        Assert.Equal(FunctionVolatility.Stable, stmt.Volatility);
        Assert.False(stmt.Strict);
    }

    [Fact]
    public void ReturnsNullOnNullInputIsStrict()
    {
        var stmt = ParseOne("""
            CREATE FUNCTION f(integer) RETURNS integer LANGUAGE sql RETURNS NULL ON NULL INPUT
            AS $$ SELECT $1 $$;
            """);

        Assert.True(stmt.Strict);
    }

    [Fact]
    public void SecurityDefiner()
    {
        var stmt = ParseOne("""
            CREATE FUNCTION f() RETURNS integer LANGUAGE plpgsql SECURITY DEFINER
            AS $$ BEGIN RETURN 1; END $$;
            """);

        Assert.True(stmt.SecurityDefiner);
    }

    [Fact]
    public void OrReplaceAndSchemaQualifiedName()
    {
        var stmt = ParseOne(
            "CREATE OR REPLACE FUNCTION app.f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.True(stmt.OrReplace);
        Assert.Equal("app", stmt.Name.Segments[0].Name);
        Assert.Equal("f", stmt.Name.Segments[^1].Name);
    }

    [Fact]
    public void NumericReturnType()
    {
        var stmt = ParseOne("""
            CREATE FUNCTION get_customer_balance(p_customer_id integer, p_effective_date timestamp)
            RETURNS numeric LANGUAGE plpgsql AS $$ BEGIN RETURN 0; END $$;
            """);

        Assert.Equal("numeric", stmt.ReturnType!.TypeName);
        Assert.Equal(2, stmt.Parameters.Count);
        Assert.Equal("p_effective_date", stmt.Parameters[1].Name?.Name);
    }

    [Fact]
    public void SourcePositionIsRecorded()
    {
        var stmt = ParseOne("\n\nCREATE FUNCTION f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.Equal(3, stmt.Line);
        Assert.Equal(1, stmt.Column);
    }
}

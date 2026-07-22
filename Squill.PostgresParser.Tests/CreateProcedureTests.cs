using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

public class CreateProcedureTests
{
    private static CreateProcedureStatement ParseOne(string text)
    {
        var parser = new AntlrPostgresParser();
        var root = parser.Parse(text);
        return Assert.IsType<CreateProcedureStatement>(Assert.Single(root.Statements));
    }

    [Fact]
    public void CreateProcedure_NoArguments()
    {
        var stmt = ParseOne("""
            CREATE PROCEDURE do_nothing()
            LANGUAGE plpgsql
            AS $$
            BEGIN
            END;
            $$;
            """);

        Assert.Equal("do_nothing", stmt.Name.Segments[^1].Name);
        Assert.Empty(stmt.Parameters);
        Assert.Equal("plpgsql", stmt.Language);
        Assert.False(stmt.OrReplace);
    }

    [Fact]
    public void CreateProcedure_OrReplace()
    {
        var stmt = ParseOne("CREATE OR REPLACE PROCEDURE p() LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.True(stmt.OrReplace);
    }

    [Fact]
    public void CreateProcedure_SchemaQualifiedName()
    {
        var stmt = ParseOne("CREATE PROCEDURE staging.reload() LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.Equal("reload", stmt.Name.Segments[^1].Name);
        Assert.Equal("staging", stmt.Name.Segments[0].Name);
        Assert.Equal(2, stmt.Name.Segments.Count);
    }

    [Fact]
    public void CreateProcedure_ParametersWithModesAndTypes()
    {
        var stmt = ParseOne(
            "CREATE PROCEDURE p(a integer, IN b text, INOUT c numeric) LANGUAGE sql AS $$ SELECT c $$;");

        Assert.Equal(3, stmt.Parameters.Count);

        Assert.Equal("a", stmt.Parameters[0].Name?.Name);
        Assert.Equal(ParameterMode.In, stmt.Parameters[0].Mode);

        Assert.Equal("b", stmt.Parameters[1].Name?.Name);
        Assert.Equal(ParameterMode.In, stmt.Parameters[1].Mode);

        Assert.Equal("c", stmt.Parameters[2].Name?.Name);
        Assert.Equal(ParameterMode.InOut, stmt.Parameters[2].Mode);
    }

    // A parameter whose type has a modifier (varchar(100)) must still bind its name to the
    // right parameter — the grammar allows `param_name? func_type` and `param_name
    // arg_class? func_type`, so a mis-read here silently shifts every name by one.
    [Fact]
    public void CreateProcedure_ParametersWithModifiedTypes()
    {
        var stmt = ParseOne(
            "CREATE PROCEDURE p(widget_id integer, widget_name varchar(100)) "
            + "LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.Equal(2, stmt.Parameters.Count);
        Assert.Equal("widget_id", stmt.Parameters[0].Name?.Name);
        Assert.Equal("widget_name", stmt.Parameters[1].Name?.Name);
    }

    // The body is captured verbatim — exactly the text between the dollar-quote
    // delimiters — because that is precisely what PostgreSQL stores in pg_proc.prosrc.
    // Preserving it byte-for-byte is what lets a parsed model hash-match an extracted one.
    [Fact]
    public void CreateProcedure_BodyIsCapturedVerbatim()
    {
        var stmt = ParseOne("""
            CREATE PROCEDURE p(a integer)
            LANGUAGE plpgsql
            AS $$
            BEGIN
              RAISE NOTICE 'hi %', a;
            END;
            $$;
            """);

        Assert.Equal("\nBEGIN\n  RAISE NOTICE 'hi %', a;\nEND;\n", stmt.Body);
    }

    [Fact]
    public void CreateProcedure_SingleQuotedBody()
    {
        var stmt = ParseOne("CREATE PROCEDURE p() LANGUAGE sql AS 'SELECT 1';");

        Assert.Equal("SELECT 1", stmt.Body);
    }

    [Fact]
    public void CreateProcedure_SecurityDefiner()
    {
        var stmt = ParseOne(
            "CREATE PROCEDURE p() LANGUAGE plpgsql SECURITY DEFINER AS $$ BEGIN NULL; END; $$;");

        Assert.True(stmt.SecurityDefiner);
    }

    [Fact]
    public void CreateProcedure_SecurityInvokerIsDefault()
    {
        var stmt = ParseOne("CREATE PROCEDURE p() LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.False(stmt.SecurityDefiner);
    }

    // A language other than plpgsql/sql is carried through unchanged, so procedures
    // written in any installed procedural language (plpython3u, plperl, …) are modeled.
    [Fact]
    public void CreateProcedure_OtherLanguage()
    {
        var stmt = ParseOne("CREATE PROCEDURE p() LANGUAGE plpython3u AS $$ pass $$;");

        Assert.Equal("plpython3u", stmt.Language);
    }

    [Fact]
    public void CreateProcedure_RecordsSourcePosition()
    {
        var stmt = ParseOne("\n\nCREATE PROCEDURE p() LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.Equal(3, stmt.Line);
        Assert.Equal(1, stmt.Column);
    }

}

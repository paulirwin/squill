using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

public class CreateProcedureModelTests
{
    private static async Task<Model> BuildModelAsync(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return (await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken)).Model;
    }

    private static async Task<Element> BuildProcedureAsync(string sql)
    {
        var model = await BuildModelAsync(sql);

        return Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlProcedure);
    }

    [Fact]
    public async Task Procedure_NoArguments_HasEmptySignature()
    {
        var procedure = await BuildProcedureAsync(
            "CREATE PROCEDURE do_nothing() LANGUAGE sql AS $$ SELECT 1 $$;");

        // The name carries the argument signature so overloads are distinct objects.
        Assert.Equal("public.do_nothing()", procedure.Name);
        Assert.Equal("do_nothing", procedure.GetProperty<string>(PostgresPropertyNames.RoutineName));
        Assert.Equal("sql", procedure.GetProperty<string>(PostgresPropertyNames.Language));
        Assert.Equal(" SELECT 1 ", procedure.GetProperty<string>(PostgresPropertyNames.Body));
        Assert.Equal("public", PostgresModelFactory.GetSchema(procedure));
    }

    // PostgreSQL normalizes type names and discards type modifiers when it records a
    // routine's argument signature, so `varchar(10)` becomes `character varying` and
    // `int` becomes `integer`. The parsed model must apply the same normalization or it
    // will not hash-match a model extracted from a live database.
    [Theory]
    [InlineData("int", "integer")]
    [InlineData("integer", "integer")]
    [InlineData("int4", "integer")]
    [InlineData("bigint", "bigint")]
    [InlineData("int8", "bigint")]
    [InlineData("smallint", "smallint")]
    [InlineData("bool", "boolean")]
    [InlineData("boolean", "boolean")]
    [InlineData("varchar(10)", "character varying")]
    [InlineData("varchar", "character varying")]
    [InlineData("character varying(5)", "character varying")]
    [InlineData("char(3)", "character")]
    [InlineData("text", "text")]
    [InlineData("numeric(5,2)", "numeric")]
    [InlineData("decimal(5,2)", "numeric")]
    [InlineData("real", "real")]
    [InlineData("float8", "double precision")]
    [InlineData("double precision", "double precision")]
    [InlineData("timestamptz", "timestamp with time zone")]
    [InlineData("timestamp", "timestamp without time zone")]
    [InlineData("date", "date")]
    [InlineData("uuid", "uuid")]
    [InlineData("jsonb", "jsonb")]
    [InlineData("text[]", "text[]")]
    public async Task Procedure_ArgumentTypesAreNormalized(string declared, string expected)
    {
        var procedure = await BuildProcedureAsync(
            $"CREATE PROCEDURE p(a {declared}) LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.Equal($"public.p({expected})", procedure.Name);
        Assert.Equal(expected, procedure.GetProperty<string>(PostgresPropertyNames.ArgumentTypes));
    }

    // The rendered parameter list must keep each name bound to its own parameter; a shift
    // here would deploy a procedure with its parameters silently renamed.
    [Fact]
    public async Task Procedure_ArgumentsRenderModeNameAndType()
    {
        var procedure = await BuildProcedureAsync(
            "CREATE PROCEDURE p(widget_id integer, widget_name varchar(100)) "
            + "LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.Equal(
            "IN widget_id integer, IN widget_name character varying",
            procedure.GetProperty<string>(PostgresPropertyNames.Arguments));
    }

    [Fact]
    public async Task Procedure_MultipleArguments_SignatureIsCommaSeparated()
    {
        var procedure = await BuildProcedureAsync(
            "CREATE PROCEDURE p(a int, b varchar(10), c numeric(5,2)) LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.Equal("public.p(integer,character varying,numeric)", procedure.Name);
    }

    // An OUT parameter does not participate in a routine's identity in PostgreSQL, but an
    // INOUT one does — matching pg_proc.proargtypes, which the extraction builder reads.
    [Fact]
    public async Task Procedure_InOutArgumentIsPartOfSignature()
    {
        var procedure = await BuildProcedureAsync(
            "CREATE PROCEDURE p(IN a int, INOUT b text) LANGUAGE sql AS $$ SELECT b $$;");

        Assert.Equal("public.p(integer,text)", procedure.Name);
    }

    [Fact]
    public async Task Procedure_Overloads_AreDistinctElements()
    {
        var model = await BuildModelAsync("""
            CREATE PROCEDURE p(a int) LANGUAGE sql AS $$ SELECT 1 $$;
            CREATE PROCEDURE p(a text) LANGUAGE sql AS $$ SELECT 1 $$;
            """);

        var procedures = model.Elements
            .Where(i => i.Type == PostgresElementTypes.SqlProcedure)
            .ToList();

        Assert.Equal(2, procedures.Count);
        Assert.Contains(procedures, i => (string?)i.Name == "public.p(integer)");
        Assert.Contains(procedures, i => (string?)i.Name == "public.p(text)");
    }

    [Fact]
    public async Task Procedure_SchemaQualified_CarriesItsSchema()
    {
        var procedure = await BuildProcedureAsync("""
            CREATE SCHEMA staging;
            CREATE PROCEDURE staging.reload() LANGUAGE sql AS $$ SELECT 1 $$;
            """);

        Assert.Equal("staging.reload()", procedure.Name);
        Assert.Equal("staging", PostgresModelFactory.GetSchema(procedure));
    }

    // A procedure in an undeclared schema is a build error, exactly as for a table —
    // Squill never creates a schema implicitly.
    [Fact]
    public async Task Procedure_InUndeclaredSchema_IsAnError()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(() =>
            BuildModelAsync("CREATE PROCEDURE missing.p() LANGUAGE sql AS $$ SELECT 1 $$;"));

        Assert.Equal(SqlSourceException.UnresolvedReference, ex.Code);
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public async Task Procedure_SecurityDefinerIsStored()
    {
        var procedure = await BuildProcedureAsync(
            "CREATE PROCEDURE p() LANGUAGE plpgsql SECURITY DEFINER AS $$ BEGIN NULL; END; $$;");

        Assert.Equal(true, procedure.GetProperty<bool?>(PostgresPropertyNames.IsSecurityDefiner));
    }

    // INVOKER is PostgreSQL's default, so it records no property — keeping the parsed
    // model's shape identical to the extracted one for the common case.
    [Fact]
    public async Task Procedure_SecurityInvokerIsNotStored()
    {
        var procedure = await BuildProcedureAsync(
            "CREATE PROCEDURE p() LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.Null(procedure.GetProperty<bool?>(PostgresPropertyNames.IsSecurityDefiner));
    }

    // A parameter DEFAULT is rewritten by PostgreSQL when stored ('x' comes back as
    // 'x'::text), so it cannot round-trip. Rejecting it at build time — anchored at the
    // statement — is better than deploying a procedure that silently loses its defaults.
    [Fact]
    public async Task Procedure_ParameterDefault_IsReportedAsASourceError()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync("""
            CREATE PROCEDURE p(a integer DEFAULT 1)
            LANGUAGE sql AS $$ SELECT 1 $$;
            """));

        Assert.Equal("Test.sql", ex.SourceFile);
        Assert.Equal(1, ex.Line);
        Assert.Contains("DEFAULT", ex.Message);
    }

    [Fact]
    public async Task CreateFunction_IsReportedAsASourceError()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync(
            "CREATE FUNCTION f() RETURNS integer LANGUAGE sql AS $$ SELECT 1 $$;"));

        Assert.Equal("Test.sql", ex.SourceFile);
        Assert.Contains("CREATE FUNCTION", ex.Message);
    }

    [Fact]
    public async Task Procedure_BodyIsStoredVerbatim()
    {
        var procedure = await BuildProcedureAsync("""
            CREATE PROCEDURE p(a integer)
            LANGUAGE plpgsql
            AS $$
            BEGIN
              RAISE NOTICE 'hi %', a;
            END;
            $$;
            """);

        Assert.Equal(
            "\nBEGIN\n  RAISE NOTICE 'hi %', a;\nEND;\n",
            procedure.GetProperty<string>(PostgresPropertyNames.Body));
    }
}

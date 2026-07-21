using Squill.MariaDbParser;
using Squill.MariaDbParser.Syntax;

namespace Squill.MariaDbParser.Tests;

/// <summary>
/// Parser-level tests for CREATE PROCEDURE (issue #41), asserting the syntax tree the
/// mapper produces. Model-level concerns (element shape, type normalization) are covered
/// in Squill.Provider.MariaDb.Tests.
/// </summary>
public class CreateProcedureTests
{
    private static CreateProcedureStatement ParseOne(string text)
    {
        var root = new AntlrMariaDbParser().Parse(text);

        return Assert.IsType<CreateProcedureStatement>(Assert.Single(root.Statements));
    }

    [Fact]
    public void CreateProcedure_NoParameters()
    {
        var statement = ParseOne("CREATE PROCEDURE do_nothing() SELECT 1;");

        Assert.Equal("do_nothing", statement.Name.Name);
        Assert.Empty(statement.Parameters);
        Assert.False(statement.OrReplace);
    }

    [Fact]
    public void CreateProcedure_OrReplace()
    {
        // OR REPLACE is MariaDB-specific syntax; it affects how the procedure is created,
        // not the schema state, so it is recorded but does not reach the model.
        var statement = ParseOne("CREATE OR REPLACE PROCEDURE p() SELECT 1;");

        Assert.True(statement.OrReplace);
    }

    [Fact]
    public void CreateProcedure_QualifiedName()
    {
        var statement = ParseOne("CREATE PROCEDURE shop.p() SELECT 1;");

        Assert.Equal(2, statement.Name.Segments.Count);
        Assert.Equal("shop", statement.Name.Segments[0].Name);
        Assert.Equal("p", statement.Name.Name);
    }

    [Fact]
    public void CreateProcedure_BacktickQuotedNameIsUnquoted()
    {
        var statement = ParseOne("CREATE PROCEDURE `my proc`() SELECT 1;");

        Assert.Equal("my proc", statement.Name.Name);
    }

    [Fact]
    public void CreateProcedure_ParameterModesAndTypes()
    {
        var statement = ParseOne(
            "CREATE PROCEDURE p(a INT, IN b VARCHAR(50), OUT c DECIMAL(10,2), INOUT d BIGINT) SELECT 1;");

        Assert.Equal(4, statement.Parameters.Count);

        // A parameter with no written mode is IN, which is the engine default.
        Assert.Equal("a", statement.Parameters[0].Name.Name);
        Assert.Equal(ParameterMode.In, statement.Parameters[0].Mode);
        Assert.Equal("int", statement.Parameters[0].DataType.TypeName);

        Assert.Equal("b", statement.Parameters[1].Name.Name);
        Assert.Equal(ParameterMode.In, statement.Parameters[1].Mode);
        Assert.Equal("varchar", statement.Parameters[1].DataType.TypeName);
        Assert.Equal([50], statement.Parameters[1].DataType.Modifiers);

        Assert.Equal("c", statement.Parameters[2].Name.Name);
        Assert.Equal(ParameterMode.Out, statement.Parameters[2].Mode);
        Assert.Equal([10, 2], statement.Parameters[2].DataType.Modifiers);

        Assert.Equal("d", statement.Parameters[3].Name.Name);
        Assert.Equal(ParameterMode.InOut, statement.Parameters[3].Mode);
    }

    [Fact]
    public void CreateProcedure_UnsignedParameter()
    {
        var statement = ParseOne("CREATE PROCEDURE p(a INT UNSIGNED) SELECT 1;");

        Assert.True(Assert.Single(statement.Parameters).DataType.IsUnsigned);
    }

    [Fact]
    public void CreateProcedure_BodyIsCapturedVerbatim()
    {
        // The body must be read back from the input stream, not rebuilt from tokens:
        // both engines return ROUTINE_DEFINITION with its original whitespace, so a
        // token-concatenated body could never hash-match an extracted model.
        var statement = ParseOne(
            """
            CREATE PROCEDURE p(IN a INT)
            BEGIN
              SET @x = a;
              SELECT @x;
            END;
            """);

        Assert.Equal(
            """
            BEGIN
              SET @x = a;
              SELECT @x;
            END
            """,
            statement.Body);
    }

    [Fact]
    public void CreateProcedure_SingleStatementBody()
    {
        var statement = ParseOne("CREATE PROCEDURE p() SELECT 1;");

        Assert.Equal("SELECT 1", statement.Body);
    }

    [Fact]
    public void CreateProcedure_BodyWithSemicolonsIsOneStatement()
    {
        // A BEGIN ... END body contains statement separators. The grammar absorbs them, so
        // the whole procedure stays a single statement rather than being split at the first
        // inner semicolon.
        var root = new AntlrMariaDbParser().Parse(
            """
            CREATE PROCEDURE p()
            BEGIN
              SELECT 1;
              SELECT 2;
            END;
            """);

        Assert.Single(root.Statements);
    }

    [Fact]
    public void CreateProcedure_DefaultsAreNotDeterministicContainsSqlAndDefiner()
    {
        // Both MariaDB and MySQL report NOT DETERMINISTIC / CONTAINS SQL / DEFINER when no
        // clause is written, so an unwritten clause must leave the defaults in place.
        var statement = ParseOne("CREATE PROCEDURE p() SELECT 1;");

        Assert.False(statement.IsDeterministic);
        Assert.Null(statement.SqlDataAccess);
        Assert.False(statement.IsSecurityInvoker);
    }

    [Fact]
    public void CreateProcedure_Deterministic()
    {
        Assert.True(ParseOne("CREATE PROCEDURE p() DETERMINISTIC SELECT 1;").IsDeterministic);
    }

    [Fact]
    public void CreateProcedure_NotDeterministicIsTheDefault()
    {
        Assert.False(ParseOne("CREATE PROCEDURE p() NOT DETERMINISTIC SELECT 1;").IsDeterministic);
    }

    [Theory]
    [InlineData("CONTAINS SQL", "CONTAINS SQL")]
    [InlineData("NO SQL", "NO SQL")]
    [InlineData("READS SQL DATA", "READS SQL DATA")]
    [InlineData("MODIFIES SQL DATA", "MODIFIES SQL DATA")]
    public void CreateProcedure_SqlDataAccessIsSpelledAsTheCatalogReportsIt(string clause, string expected)
    {
        var statement = ParseOne($"CREATE PROCEDURE p() {clause} SELECT 1;");

        Assert.Equal(expected, statement.SqlDataAccess);
    }

    [Fact]
    public void CreateProcedure_SecurityInvoker()
    {
        Assert.True(ParseOne("CREATE PROCEDURE p() SQL SECURITY INVOKER SELECT 1;").IsSecurityInvoker);
    }

    [Fact]
    public void CreateProcedure_SecurityDefinerIsTheDefault()
    {
        Assert.False(ParseOne("CREATE PROCEDURE p() SQL SECURITY DEFINER SELECT 1;").IsSecurityInvoker);
    }

    [Fact]
    public void CreateProcedure_CommentAndLanguageAreIgnored()
    {
        // Neither is a schema facet Squill tracks, and LANGUAGE SQL is the only language
        // either engine supports.
        var statement = ParseOne(
            "CREATE PROCEDURE p() COMMENT 'hello' LANGUAGE SQL DETERMINISTIC SELECT 1;");

        Assert.True(statement.IsDeterministic);
        Assert.Equal("SELECT 1", statement.Body);
    }

    [Fact]
    public void CreateProcedure_MultipleOptions()
    {
        var statement = ParseOne(
            """
            CREATE PROCEDURE p(IN a INT)
              DETERMINISTIC
              MODIFIES SQL DATA
              SQL SECURITY INVOKER
              DELETE FROM t WHERE id = a;
            """);

        Assert.True(statement.IsDeterministic);
        Assert.Equal("MODIFIES SQL DATA", statement.SqlDataAccess);
        Assert.True(statement.IsSecurityInvoker);
        Assert.Equal("DELETE FROM t WHERE id = a", statement.Body);
    }

    [Fact]
    public void CreateProcedure_RecordsSourcePosition()
    {
        var statement = ParseOne(
            """


            CREATE PROCEDURE p() SELECT 1;
            """);

        Assert.Equal(3, statement.Line);
        Assert.Equal(1, statement.Column);
    }

    [Fact]
    public void CreateProcedure_AlongsideOtherStatements()
    {
        var root = new AntlrMariaDbParser().Parse(
            """
            CREATE TABLE t (id INT PRIMARY KEY);

            CREATE PROCEDURE p()
            BEGIN
              INSERT INTO t (id) VALUES (1);
            END;

            CREATE INDEX ix_t ON t (id);
            """);

        Assert.Collection(
            root.Statements,
            i => Assert.IsType<CreateTableStatement>(i),
            i => Assert.IsType<CreateProcedureStatement>(i),
            i => Assert.IsType<CreateIndexStatement>(i));
    }

    [Fact]
    public void CreateFunction_ParsesAsItsOwnStatement()
    {
        // Functions are not modeled, but are parsed into a marker so the model builder can
        // report them as unsupported at their source position rather than dropping them.
        var root = new AntlrMariaDbParser().Parse(
            "CREATE FUNCTION f(a INT) RETURNS INT RETURN a + 1;");

        var statement = Assert.IsType<CreateFunctionStatement>(Assert.Single(root.Statements));

        Assert.Equal("f", statement.Name.Name);
    }
}

using Squill.MariaDbParser;
using Squill.MariaDbParser.Syntax;
using Squill.TestFramework;

namespace Squill.MariaDbParser.Tests;

/// <summary>
/// Parser-level tests for CREATE FUNCTION (issue #74), asserting the syntax tree the mapper
/// produces. Model-level concerns (element shape, type normalization, script generation) are
/// covered in Squill.Provider.MariaDb.Tests. Mirrors <see cref="CreateProcedureTests"/>.
/// </summary>
public class CreateFunctionTests
{
    private static CreateFunctionStatement ParseOne(string text)
        => ParseAssertions.Single<CreateFunctionStatement>(new AntlrMariaDbParser().Parse(text).Statements);

    [Fact]
    public void CreateFunction_NoParameters()
    {
        var statement = ParseOne("CREATE FUNCTION answer() RETURNS INT RETURN 42;");

        Assert.Equal("answer", statement.Name.Name);
        Assert.Empty(statement.Parameters);
        Assert.Equal("int", statement.ReturnType.TypeName);
        Assert.False(statement.OrReplace);
    }

    [Fact]
    public void CreateFunction_OrReplace()
    {
        // OR REPLACE is MariaDB-specific syntax; it affects how the function is created, not
        // the schema state, so it is recorded but does not reach the model.
        var statement = ParseOne("CREATE OR REPLACE FUNCTION f() RETURNS INT RETURN 1;");

        Assert.True(statement.OrReplace);
    }

    [Fact]
    public void CreateFunction_QualifiedName()
    {
        var statement = ParseOne("CREATE FUNCTION shop.f() RETURNS INT RETURN 1;");

        Assert.Equal(2, statement.Name.Segments.Count);
        Assert.Equal("shop", statement.Name.Segments[0].Name);
        Assert.Equal("f", statement.Name.Name);
    }

    [Fact]
    public void CreateFunction_BacktickQuotedNameIsUnquoted()
    {
        var statement = ParseOne("CREATE FUNCTION `my func`() RETURNS INT RETURN 1;");

        Assert.Equal("my func", statement.Name.Name);
    }

    [Fact]
    public void CreateFunction_ParametersAreAlwaysIn()
    {
        // Unlike a procedure, a function parameter has no direction — it is always IN.
        var statement = ParseOne(
            "CREATE FUNCTION f(a INT, b VARCHAR(50), c DECIMAL(10,2)) RETURNS INT RETURN a;");

        Assert.Equal(3, statement.Parameters.Count);

        Assert.All(statement.Parameters, p => Assert.Equal(ParameterMode.In, p.Mode));

        Assert.Equal("a", statement.Parameters[0].Name.Name);
        Assert.Equal("int", statement.Parameters[0].DataType.TypeName);

        Assert.Equal("b", statement.Parameters[1].Name.Name);
        Assert.Equal("varchar", statement.Parameters[1].DataType.TypeName);
        Assert.Equal([50], statement.Parameters[1].DataType.Modifiers);

        Assert.Equal("c", statement.Parameters[2].Name.Name);
        Assert.Equal([10, 2], statement.Parameters[2].DataType.Modifiers);
    }

    [Fact]
    public void CreateFunction_UnsignedParameter()
    {
        var statement = ParseOne("CREATE FUNCTION f(a INT UNSIGNED) RETURNS INT RETURN a;");

        Assert.True(Assert.Single(statement.Parameters).DataType.IsUnsigned);
    }

    [Fact]
    public void CreateFunction_ReturnTypeWithLength()
    {
        var statement = ParseOne("CREATE FUNCTION f() RETURNS VARCHAR(100) RETURN 'x';");

        Assert.Equal("varchar", statement.ReturnType.TypeName);
        Assert.Equal([100], statement.ReturnType.Modifiers);
    }

    [Fact]
    public void CreateFunction_ReturnTypeWithPrecisionAndScale()
    {
        var statement = ParseOne("CREATE FUNCTION f() RETURNS DECIMAL(10,2) RETURN 1.5;");

        Assert.Equal("decimal", statement.ReturnType.TypeName);
        Assert.Equal([10, 2], statement.ReturnType.Modifiers);
    }

    [Fact]
    public void CreateFunction_BareReturnBodyIsCapturedVerbatim()
    {
        var statement = ParseOne("CREATE FUNCTION f(a INT) RETURNS INT RETURN a + 1;");

        Assert.Equal("RETURN a + 1", statement.Body);
    }

    [Fact]
    public void CreateFunction_BeginEndBodyIsCapturedVerbatim()
    {
        // The body must be read back from the input stream, not rebuilt from tokens: both
        // engines return ROUTINE_DEFINITION with its original whitespace, and the RETURNS
        // clause is not part of it.
        var statement = ParseOne(
            """
            CREATE FUNCTION greet(who VARCHAR(50)) RETURNS VARCHAR(100)
            BEGIN
              DECLARE msg VARCHAR(100);
              SET msg = CONCAT('Hi ', who);
              RETURN msg;
            END;
            """);

        Assert.Equal(
            """
            BEGIN
              DECLARE msg VARCHAR(100);
              SET msg = CONCAT('Hi ', who);
              RETURN msg;
            END
            """,
            statement.Body);
    }

    [Fact]
    public void CreateFunction_BodyWithSemicolonsIsOneStatement()
    {
        var root = new AntlrMariaDbParser().Parse(
            """
            CREATE FUNCTION f() RETURNS INT
            BEGIN
              DECLARE x INT;
              SET x = 1;
              RETURN x;
            END;
            """);

        Assert.Single(root.Statements);
    }

    [Fact]
    public void CreateFunction_DefaultsAreNotDeterministicContainsSqlAndDefiner()
    {
        var statement = ParseOne("CREATE FUNCTION f() RETURNS INT RETURN 1;");

        Assert.False(statement.IsDeterministic);
        Assert.Null(statement.SqlDataAccess);
        Assert.False(statement.IsSecurityInvoker);
    }

    [Fact]
    public void CreateFunction_Deterministic()
    {
        Assert.True(
            ParseOne("CREATE FUNCTION f() RETURNS INT DETERMINISTIC RETURN 1;").IsDeterministic);
    }

    [Theory]
    [InlineData("CONTAINS SQL", "CONTAINS SQL")]
    [InlineData("NO SQL", "NO SQL")]
    [InlineData("READS SQL DATA", "READS SQL DATA")]
    [InlineData("MODIFIES SQL DATA", "MODIFIES SQL DATA")]
    public void CreateFunction_SqlDataAccessIsSpelledAsTheCatalogReportsIt(string clause, string expected)
    {
        var statement = ParseOne($"CREATE FUNCTION f() RETURNS INT {clause} RETURN 1;");

        Assert.Equal(expected, statement.SqlDataAccess);
    }

    [Fact]
    public void CreateFunction_SecurityInvoker()
    {
        Assert.True(
            ParseOne("CREATE FUNCTION f() RETURNS INT SQL SECURITY INVOKER RETURN 1;")
                .IsSecurityInvoker);
    }

    [Fact]
    public void CreateFunction_MultipleOptions()
    {
        var statement = ParseOne(
            """
            CREATE FUNCTION f(a INT) RETURNS INT
              DETERMINISTIC
              READS SQL DATA
              SQL SECURITY INVOKER
              RETURN a;
            """);

        Assert.True(statement.IsDeterministic);
        Assert.Equal("READS SQL DATA", statement.SqlDataAccess);
        Assert.True(statement.IsSecurityInvoker);
        Assert.Equal("RETURN a", statement.Body);
    }

    [Fact]
    public void CreateFunction_BareReturnAsHandlerActionParses()
    {
        // Regression for #101: a bare `RETURN` used as a DECLARE ... HANDLER action must parse.
        // The upstream grammar's handler action was `routineBody`, which rejected this form with
        // "no viable alternative"; widened to accept a bare sqlStatement (antlr/grammars-v4#4949).
        // This is the canonical Sakila `inventory_held_by_customer` shape.
        var statement = ParseOne(
            """
            CREATE FUNCTION inventory_held_by_customer(p_inventory_id int) RETURNS int READS SQL DATA
            BEGIN
                DECLARE v_customer_id INT;
                DECLARE EXIT HANDLER FOR NOT FOUND RETURN NULL;

                SELECT customer_id INTO v_customer_id FROM rental WHERE inventory_id = p_inventory_id;
                RETURN v_customer_id;
            END;
            """);

        Assert.Equal("inventory_held_by_customer", statement.Name.Name);
    }

    [Fact]
    public void CreateFunction_BeginEndHandlerActionStillParses()
    {
        // The pre-fix workaround form (a compound statement action) must keep parsing after the
        // grammar was widened to `(compoundStatement | sqlStatement)`.
        var statement = ParseOne(
            """
            CREATE FUNCTION f() RETURNS INT
            BEGIN
                DECLARE EXIT HANDLER FOR NOT FOUND BEGIN RETURN NULL; END;
                RETURN 1;
            END;
            """);

        Assert.Equal("f", statement.Name.Name);
    }

    [Fact]
    public void CreateFunction_SetAsHandlerActionStillParses()
    {
        // A plain SET as the handler action (an sqlStatement) must also parse.
        var statement = ParseOne(
            """
            CREATE FUNCTION f() RETURNS INT
            BEGIN
                DECLARE done INT DEFAULT 0;
                DECLARE CONTINUE HANDLER FOR NOT FOUND SET done = 1;
                RETURN done;
            END;
            """);

        Assert.Equal("f", statement.Name.Name);
    }

    [Fact]
    public void CreateFunction_RecordsSourcePosition()
    {
        var statement = ParseOne(
            """


            CREATE FUNCTION f() RETURNS INT RETURN 1;
            """);

        Assert.Equal(3, statement.Line);
        Assert.Equal(1, statement.Column);
    }
}

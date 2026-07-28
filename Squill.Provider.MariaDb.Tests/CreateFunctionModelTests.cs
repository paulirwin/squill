using Squill.Core;
using Squill.TestFramework;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Tests that CREATE FUNCTION maps to the expected model element (issue #74). Mirrors
/// <see cref="CreateProcedureModelTests"/>: the type normalization asserted here is what lets
/// one parsed model hash-match a model extracted from either engine, which report a routine's
/// parameter and return types differently.
/// </summary>
public class CreateFunctionModelTests
{
    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser(), MariaDbEngine.MariaDb),
            TestContext.Current.CancellationToken);

    private static async Task<Element> BuildFunctionAsync(string sql)
    {
        var model = await BuildModelAsync(sql);

        return Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlFunction);
    }

    [Fact]
    public async Task Function_IsModeledWithReturnTypeAndBody()
    {
        var function = await BuildFunctionAsync(
            "CREATE FUNCTION add_one(a INT) RETURNS INT DETERMINISTIC RETURN a + 1;");

        Assert.Equal("add_one", function.Name);
        Assert.Equal("IN a int", function.GetRequiredProperty<string>(MariaDbPropertyNames.Arguments));
        Assert.Equal("int", function.GetRequiredProperty<string>(MariaDbPropertyNames.ReturnType));
        Assert.Equal("RETURN a + 1", function.GetRequiredProperty<string>(MariaDbPropertyNames.Body));
    }

    [Fact]
    public async Task Function_NoParameters()
    {
        var function = await BuildFunctionAsync("CREATE FUNCTION answer() RETURNS INT RETURN 42;");

        Assert.Empty(function.GetRequiredProperty<string>(MariaDbPropertyNames.Arguments));
    }

    [Fact]
    public async Task Function_NameIsNotQualified()
    {
        // A function is not schema-scoped within a database, so a db qualifier is dropped
        // exactly as it is for a table.
        var function = await BuildFunctionAsync("CREATE FUNCTION shop.f() RETURNS INT RETURN 1;");

        Assert.Equal("f", function.Name);
    }

    [Fact]
    public async Task Function_ArgumentsAreAlwaysIn()
    {
        var function = await BuildFunctionAsync(
            "CREATE FUNCTION f(widget_id INT, label VARCHAR(50)) RETURNS INT RETURN widget_id;");

        Assert.Equal(
            "IN widget_id int, IN label varchar(50)",
            function.GetRequiredProperty<string>(MariaDbPropertyNames.Arguments));
    }

    [Theory]
    // An integer display width is discarded: MariaDB reports int(11) and MySQL reports int
    // for the same return type, so keeping it would make a model engine-specific.
    [InlineData("INT", "int")]
    [InlineData("INT(11)", "int")]
    [InlineData("INTEGER", "int")]
    [InlineData("BIGINT", "bigint")]
    [InlineData("INT UNSIGNED", "int unsigned")]
    [InlineData("BOOL", "tinyint(1)")]
    // A length or precision/scale is meaningful and is kept as written.
    [InlineData("VARCHAR(100)", "varchar(100)")]
    [InlineData("CHAR(3)", "char(3)")]
    [InlineData("DECIMAL(10,2)", "decimal(10,2)")]
    [InlineData("NUMERIC(10,2)", "decimal(10,2)")]
    [InlineData("TEXT", "text")]
    [InlineData("DATETIME", "datetime")]
    // MariaDB stores a JSON return type as longtext; MySQL keeps a distinct json type, so
    // folding it here is what lets a JSON return type round-trip on both engines.
    [InlineData("JSON", "longtext")]
    public async Task Function_ReturnTypeIsNormalized(string declared, string expected)
    {
        var function = await BuildFunctionAsync(
            $"CREATE FUNCTION f() RETURNS {declared} RETURN NULL;");

        Assert.Equal(expected, function.GetRequiredProperty<string>(MariaDbPropertyNames.ReturnType));
    }

    [Fact]
    public async Task Function_BodyIsStoredVerbatim()
    {
        var function = await BuildFunctionAsync(
            """
            CREATE FUNCTION greet(who VARCHAR(50)) RETURNS VARCHAR(100)
            BEGIN
              RETURN CONCAT('Hi ', who);
            END;
            """);

        Assert.Equal(
            """
            BEGIN
              RETURN CONCAT('Hi ', who);
            END
            """,
            function.GetRequiredProperty<string>(MariaDbPropertyNames.Body));
    }

    [Fact]
    public async Task Function_DefaultCharacteristicsAreNotStored()
    {
        var function = await BuildFunctionAsync("CREATE FUNCTION f() RETURNS INT RETURN 1;");

        Assert.Null(function.GetProperty<bool?>(MariaDbPropertyNames.IsDeterministic));
        Assert.Null(function.GetProperty<string>(MariaDbPropertyNames.SqlDataAccess));
        Assert.Null(function.GetProperty<bool?>(MariaDbPropertyNames.IsSecurityInvoker));
    }

    [Fact]
    public async Task Function_NonDefaultCharacteristicsAreStored()
    {
        var function = await BuildFunctionAsync(
            """
            CREATE FUNCTION f() RETURNS INT
              DETERMINISTIC
              READS SQL DATA
              SQL SECURITY INVOKER
              RETURN 1;
            """);

        Assert.Equal(true, function.GetProperty<bool?>(MariaDbPropertyNames.IsDeterministic));
        Assert.Equal("READS SQL DATA", function.GetProperty<string>(MariaDbPropertyNames.SqlDataAccess));
        Assert.Equal(true, function.GetProperty<bool?>(MariaDbPropertyNames.IsSecurityInvoker));
    }

    [Fact]
    public async Task DuplicateFunction_IsABuildError()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(() =>
            BuildModelAsync(
                """
                CREATE FUNCTION f() RETURNS INT RETURN 1;
                CREATE FUNCTION f() RETURNS INT RETURN 2;
                """));

        Assert.Equal(SqlSourceException.DuplicateDefinition, ex.Code);
        Assert.Contains("f", ex.Message);
    }

    [Fact]
    public async Task Routines_AreOrderedTogetherAfterEverythingElseByName()
    {
        // The DB extractor emits functions and procedures together last, ordered by name,
        // because the catalog has no notion of declaration order. The Merkle hash is
        // order-sensitive, so a parsed model has to adopt the same order.
        var model = await BuildModelAsync(
            """
            CREATE FUNCTION zeta() RETURNS INT RETURN 1;

            CREATE TABLE widgets (id INT PRIMARY KEY);

            CREATE PROCEDURE alpha() SELECT 2;

            CREATE FUNCTION beta() RETURNS INT RETURN 3;
            """);

        var routines = model.Elements
            .Where(i => i.Type is MariaDbElementTypes.SqlFunction or MariaDbElementTypes.SqlProcedure)
            .Select(i => i.Name as string)
            .ToList();

        // Ordered by name across both routine kinds, not grouped by kind.
        Assert.Equal(["alpha", "beta", "zeta"], routines);

        // Every routine comes after every non-routine element.
        var lastNonRoutine = model.Elements
            .Select((element, index) => (element, index))
            .Last(i => i.element.Type is not MariaDbElementTypes.SqlFunction
                and not MariaDbElementTypes.SqlProcedure).index;

        var firstRoutine = model.Elements
            .Select((element, index) => (element, index))
            .First(i => i.element.Type is MariaDbElementTypes.SqlFunction
                or MariaDbElementTypes.SqlProcedure).index;

        Assert.True(firstRoutine > lastNonRoutine);
    }

    [Fact]
    public async Task Function_DoesNotBecomeATableDependent()
    {
        var model = await BuildModelAsync(
            """
            CREATE TABLE zebra (id INT PRIMARY KEY);

            CREATE FUNCTION f() RETURNS INT RETURN 1;

            CREATE TABLE apple (id INT PRIMARY KEY);
            """);

        Assert.Equal(MariaDbElementTypes.SqlFunction, model.Elements[^1].Type);
    }

    [Fact]
    public async Task FunctionAndProcedure_MayShareAName()
    {
        // A function and a procedure occupy different namespaces, so the same name is legal.
        var model = await BuildModelAsync(
            """
            CREATE FUNCTION thing() RETURNS INT RETURN 1;
            CREATE PROCEDURE thing() SELECT 1;
            """);

        Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlFunction);
        Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlProcedure);
    }
}

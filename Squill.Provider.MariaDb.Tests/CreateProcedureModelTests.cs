using Squill.Core;
using Squill.TestFramework;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Tests that CREATE PROCEDURE maps to the expected model element (issue #41). The type
/// normalization asserted here is what lets one parsed model hash-match a model extracted
/// from either engine — MariaDB and MySQL report a routine parameter's type differently.
/// </summary>
public class CreateProcedureModelTests
{
    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser(), MariaDbEngine.MariaDb),
            TestContext.Current.CancellationToken);

    private static async Task<Element> BuildProcedureAsync(string sql)
    {
        var model = await BuildModelAsync(sql);

        return Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlProcedure);
    }

    [Fact]
    public async Task Procedure_NoParameters()
    {
        var procedure = await BuildProcedureAsync("CREATE PROCEDURE do_nothing() SELECT 1;");

        Assert.Equal("do_nothing", procedure.Name);
        Assert.Empty(procedure.GetRequiredProperty<string>(MariaDbPropertyNames.Arguments));
        Assert.Equal("SELECT 1", procedure.GetRequiredProperty<string>(MariaDbPropertyNames.Body));
    }

    [Fact]
    public async Task Procedure_NameIsNotQualified()
    {
        // A procedure is not schema-scoped within a database, so a db qualifier is dropped
        // exactly as it is for a table.
        var procedure = await BuildProcedureAsync("CREATE PROCEDURE shop.p() SELECT 1;");

        Assert.Equal("p", procedure.Name);
    }

    [Fact]
    public async Task Procedure_ArgumentsRenderModeNameAndType()
    {
        var procedure = await BuildProcedureAsync(
            "CREATE PROCEDURE p(widget_id INT, OUT widget_name VARCHAR(50)) SELECT 1;");

        Assert.Equal(
            "IN widget_id int, OUT widget_name varchar(50)",
            procedure.GetRequiredProperty<string>(MariaDbPropertyNames.Arguments));
    }

    [Theory]
    // An integer display width is discarded: MariaDB reports int(11) and MySQL reports int
    // for the same parameter, so keeping it would make a model engine-specific.
    [InlineData("TINYINT", "tinyint")]
    [InlineData("TINYINT(4)", "tinyint")]
    [InlineData("SMALLINT", "smallint")]
    [InlineData("MEDIUMINT", "mediumint")]
    [InlineData("INT", "int")]
    [InlineData("INT(11)", "int")]
    [InlineData("INTEGER", "int")]
    [InlineData("BIGINT", "bigint")]
    [InlineData("INT UNSIGNED", "int unsigned")]
    // tinyint(1) is kept: it is how both engines record BOOL, and dropping the width would
    // make BOOL and TINYINT indistinguishable.
    [InlineData("BOOL", "tinyint(1)")]
    [InlineData("BOOLEAN", "tinyint(1)")]
    [InlineData("TINYINT(1)", "tinyint(1)")]
    // A length or precision/scale is meaningful and is kept as written.
    [InlineData("VARCHAR(50)", "varchar(50)")]
    [InlineData("CHAR(3)", "char(3)")]
    [InlineData("DECIMAL(10,2)", "decimal(10,2)")]
    [InlineData("NUMERIC(10,2)", "decimal(10,2)")]
    [InlineData("DEC(10,2)", "decimal(10,2)")]
    [InlineData("FLOAT", "float")]
    [InlineData("DOUBLE", "double")]
    [InlineData("TEXT", "text")]
    [InlineData("BLOB", "blob")]
    [InlineData("DATE", "date")]
    [InlineData("DATETIME", "datetime")]
    [InlineData("TIMESTAMP", "timestamp")]
    [InlineData("TIME", "time")]
    // MariaDB stores a JSON parameter as longtext; MySQL keeps a distinct json type, so
    // folding it here is what lets a JSON parameter round-trip on both engines.
    [InlineData("JSON", "longtext")]
    public async Task Procedure_ParameterTypesAreNormalized(string declared, string expected)
    {
        var procedure = await BuildProcedureAsync($"CREATE PROCEDURE p(a {declared}) SELECT 1;");

        Assert.Equal(
            $"IN a {expected}",
            procedure.GetRequiredProperty<string>(MariaDbPropertyNames.Arguments));
    }

    [Fact]
    public async Task Procedure_BodyIsStoredVerbatim()
    {
        var procedure = await BuildProcedureAsync(
            """
            CREATE PROCEDURE p(IN a INT)
            BEGIN
              INSERT INTO t (id) VALUES (a);
              SELECT COUNT(*) FROM t;
            END;
            """);

        Assert.Equal(
            """
            BEGIN
              INSERT INTO t (id) VALUES (a);
              SELECT COUNT(*) FROM t;
            END
            """,
            procedure.GetRequiredProperty<string>(MariaDbPropertyNames.Body));
    }

    [Fact]
    public async Task Procedure_DefaultCharacteristicsAreNotStored()
    {
        // Both engines default to NOT DETERMINISTIC / CONTAINS SQL / DEFINER, so an
        // unadorned procedure must produce an element carrying none of those facets — that
        // is what keeps its shape identical to the extracted one.
        var procedure = await BuildProcedureAsync("CREATE PROCEDURE p() SELECT 1;");

        Assert.Null(procedure.GetProperty<bool?>(MariaDbPropertyNames.IsDeterministic));
        Assert.Null(procedure.GetProperty<string>(MariaDbPropertyNames.SqlDataAccess));
        Assert.Null(procedure.GetProperty<bool?>(MariaDbPropertyNames.IsSecurityInvoker));
    }

    [Fact]
    public async Task Procedure_ExplicitDefaultsAreNotStoredEither()
    {
        var procedure = await BuildProcedureAsync(
            "CREATE PROCEDURE p() NOT DETERMINISTIC CONTAINS SQL SQL SECURITY DEFINER SELECT 1;");

        Assert.Null(procedure.GetProperty<bool?>(MariaDbPropertyNames.IsDeterministic));
        Assert.Null(procedure.GetProperty<string>(MariaDbPropertyNames.SqlDataAccess));
        Assert.Null(procedure.GetProperty<bool?>(MariaDbPropertyNames.IsSecurityInvoker));
    }

    [Fact]
    public async Task Procedure_NonDefaultCharacteristicsAreStored()
    {
        var procedure = await BuildProcedureAsync(
            """
            CREATE PROCEDURE p()
              DETERMINISTIC
              MODIFIES SQL DATA
              SQL SECURITY INVOKER
              DELETE FROM t;
            """);

        Assert.Equal(true, procedure.GetProperty<bool?>(MariaDbPropertyNames.IsDeterministic));
        Assert.Equal("MODIFIES SQL DATA", procedure.GetProperty<string>(MariaDbPropertyNames.SqlDataAccess));
        Assert.Equal(true, procedure.GetProperty<bool?>(MariaDbPropertyNames.IsSecurityInvoker));
    }

    [Fact]
    public async Task Procedures_AreOrderedAfterEverythingElseByName()
    {
        // The DB extractor emits procedures last, ordered by name, because the catalog has
        // no notion of declaration order. The Merkle hash is order-sensitive, so a parsed
        // model has to adopt the same order.
        var model = await BuildModelAsync(
            """
            CREATE PROCEDURE zeta() SELECT 1;

            CREATE TABLE widgets (id INT PRIMARY KEY);

            CREATE PROCEDURE alpha() SELECT 2;
            """);

        var types = model.Elements.Select(i => $"{i.Type}:{i.Name}").ToList();

        Assert.Equal(
            [MariaDbElementTypes.SqlProcedure + ":alpha", MariaDbElementTypes.SqlProcedure + ":zeta"],
            types.Where(i => i.StartsWith(MariaDbElementTypes.SqlProcedure)).ToList());

        // Every procedure comes after every non-procedure element.
        var lastNonProcedure = model.Elements
            .Select((element, index) => (element, index))
            .Last(i => i.element.Type != MariaDbElementTypes.SqlProcedure).index;

        var firstProcedure = model.Elements
            .Select((element, index) => (element, index))
            .First(i => i.element.Type == MariaDbElementTypes.SqlProcedure).index;

        Assert.True(firstProcedure > lastNonProcedure);
    }

    [Fact]
    public async Task Procedure_DoesNotBecomeATableDependent()
    {
        // A procedure declared after a table must not be swallowed into that table's group
        // when tables are sorted by name, which would reorder it by the table's name.
        var model = await BuildModelAsync(
            """
            CREATE TABLE zebra (id INT PRIMARY KEY);

            CREATE PROCEDURE p() SELECT 1;

            CREATE TABLE apple (id INT PRIMARY KEY);
            """);

        Assert.Equal(MariaDbElementTypes.SqlProcedure, model.Elements[^1].Type);
    }
}

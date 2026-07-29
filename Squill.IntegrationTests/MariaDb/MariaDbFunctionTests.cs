using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// End-to-end stored function tests for the MariaDB provider (issue #74), run against a real
/// MariaDB or MySQL server. Each test parses declarative SQL into a model, publishes it into a
/// fresh database, extracts the database's model, and asserts the two hash-match. Mirrors
/// <see cref="MariaDbProcedureTests"/>.
///
/// Running every scenario on both engines is what proves the type normalization: the two
/// report a routine's parameter and return types differently (MariaDB keeps an integer
/// display width, MySQL does not), so a model that hash-matches on both is one that carries
/// neither engine's spelling.
///
/// The scenarios live on this abstract base and run once per engine via the concrete
/// <c>MariaDb*</c> / <c>MySql*</c> subclasses at the bottom of this file.
/// </summary>
public abstract class MariaDbFunctionTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    private Model ParseModel(string sql, CancellationToken cancellationToken)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser(), Fixture.SchemaProviderOf())
            .ExtractModelAsync(cancellationToken).GetAwaiter().GetResult().Model;
    }

    // Parses the given SQL, publishes it into a fresh database, and asserts the re-extracted
    // model hash-matches the parsed one. Returns the extracted model for further assertions.
    private async Task<Model> AssertRoundTripAsync(string sql, CancellationToken cancellationToken)
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var model = ParseModel(sql, cancellationToken);
        return await RoundTripHarness.AssertRoundTripAsync(
            provider, model, Fixture.EngineName, assertRedeployNoOp: true,
            cancellationToken: cancellationToken);
    }

    [Fact]
    public async Task SimpleFunction_RoundTrips()
    {
        var model = await AssertRoundTripAsync(
            "CREATE FUNCTION answer() RETURNS int DETERMINISTIC RETURN 42;",
            TestContext.Current.CancellationToken);

        var function = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlFunction);

        Assert.Equal("answer", function.Name);
        Assert.Empty(function.GetRequiredProperty<string>(MariaDbPropertyNames.Arguments));
        Assert.Equal("int", function.GetRequiredProperty<string>(MariaDbPropertyNames.ReturnType));
    }

    [Fact]
    public async Task FunctionWithParametersAndReturnType_RoundTrips()
    {
        var model = await AssertRoundTripAsync(
            "CREATE FUNCTION add_tax(price decimal(10,2), rate decimal(4,2)) "
            + "RETURNS decimal(10,2) DETERMINISTIC RETURN price + (price * rate);",
            TestContext.Current.CancellationToken);

        var function = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlFunction);

        Assert.Equal(
            "IN price decimal(10,2), IN rate decimal(4,2)",
            function.GetRequiredProperty<string>(MariaDbPropertyNames.Arguments));
        Assert.Equal("decimal(10,2)", function.GetRequiredProperty<string>(MariaDbPropertyNames.ReturnType));
    }

    [Fact]
    public async Task FunctionWithBeginEndBody_RoundTrips()
    {
        // A multi-statement body contains its own semicolons; the deploy path sends each
        // delta as a single command, so it must survive unsplit.
        var model = await AssertRoundTripAsync(
            """
            CREATE FUNCTION greet(who varchar(50)) RETURNS varchar(100)
            DETERMINISTIC
            BEGIN
              DECLARE msg varchar(100);
              SET msg = CONCAT('Hi ', who);
              RETURN msg;
            END;
            """,
            TestContext.Current.CancellationToken);

        var function = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlFunction);

        Assert.Contains(
            "SET msg = CONCAT('Hi ', who);",
            function.GetRequiredProperty<string>(MariaDbPropertyNames.Body));
    }

    [Fact]
    public async Task FunctionReferencingATable_IsCreatedAfterIt()
    {
        // The function is declared before the table it reads. Creating it first would fail,
        // so this proves functions are ordered last on publish.
        await AssertRoundTripAsync(
            """
            CREATE FUNCTION widget_count() RETURNS int
              READS SQL DATA
              RETURN (SELECT COUNT(*) FROM widgets);

            CREATE TABLE widgets (id int NOT NULL PRIMARY KEY);
            """,
            TestContext.Current.CancellationToken);
    }

    [Theory]
    // The integer types are where the engines disagree: MariaDB reports int(11) and MySQL
    // reports int for the same return type. Both must produce the same model.
    [InlineData("tinyint", "tinyint")]
    [InlineData("smallint", "smallint")]
    [InlineData("int", "int")]
    [InlineData("bigint", "bigint")]
    [InlineData("int unsigned", "int unsigned")]
    [InlineData("bool", "tinyint(1)")]
    [InlineData("decimal(10,2)", "decimal(10,2)")]
    [InlineData("varchar(50)", "varchar(50)")]
    [InlineData("char(3)", "char(3)")]
    [InlineData("datetime", "datetime")]
    [InlineData("double", "double")]
    public async Task FunctionReturnTypes_RoundTripIdenticallyOnBothEngines(
        string declared, string expected)
    {
        var model = await AssertRoundTripAsync(
            $"CREATE FUNCTION f() RETURNS {declared} DETERMINISTIC RETURN NULL;",
            TestContext.Current.CancellationToken);

        var function = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlFunction);

        Assert.Equal(expected, function.GetRequiredProperty<string>(MariaDbPropertyNames.ReturnType));
    }

    [Fact]
    public async Task FunctionCharacteristics_RoundTrip()
    {
        var model = await AssertRoundTripAsync(
            """
            CREATE FUNCTION f() RETURNS int
              DETERMINISTIC
              READS SQL DATA
              SQL SECURITY INVOKER
              RETURN 1;
            """,
            TestContext.Current.CancellationToken);

        var function = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlFunction);

        Assert.Equal(true, function.GetProperty<bool?>(MariaDbPropertyNames.IsDeterministic));
        Assert.Equal("READS SQL DATA", function.GetProperty<string>(MariaDbPropertyNames.SqlDataAccess));
        Assert.Equal(true, function.GetProperty<bool?>(MariaDbPropertyNames.IsSecurityInvoker));
    }

    [Fact]
    public async Task DefaultCharacteristics_AreNotStored()
    {
        // Both engines report NOT DETERMINISTIC / SQL SECURITY DEFINER for a function that
        // does not write them; if those were stored, the extracted element would carry facets
        // the parsed one does not and the round trip would fail. Only a data-access clause is
        // written here — MySQL refuses to create a function with none of DETERMINISTIC / NO
        // SQL / READS SQL DATA when binary logging is on — and NO SQL is deliberately a
        // non-default value, so it is the one characteristic expected to survive.
        var model = await AssertRoundTripAsync(
            "CREATE FUNCTION f() RETURNS int NO SQL RETURN 1;",
            TestContext.Current.CancellationToken);

        var function = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlFunction);

        Assert.Null(function.GetProperty<bool?>(MariaDbPropertyNames.IsDeterministic));
        Assert.Equal("NO SQL", function.GetProperty<string>(MariaDbPropertyNames.SqlDataAccess));
        Assert.Null(function.GetProperty<bool?>(MariaDbPropertyNames.IsSecurityInvoker));
    }

    [Fact]
    public async Task FunctionsAndProcedures_RoundTripTogetherInNameOrder()
    {
        // Both routine kinds are extracted together, ordered by name — not grouped by kind.
        var model = await AssertRoundTripAsync(
            """
            CREATE FUNCTION zeta() RETURNS int DETERMINISTIC RETURN 1;
            CREATE PROCEDURE alpha() SELECT 2;
            CREATE FUNCTION middle() RETURNS int DETERMINISTIC RETURN 3;
            """,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["alpha", "middle", "zeta"],
            model.Elements
                .Where(i => i.Type is MariaDbElementTypes.SqlFunction or MariaDbElementTypes.SqlProcedure)
                .Select(i => i.Name as string)
                .ToList());
    }

    [Fact]
    public async Task ChangedFunctionBody_IsReplacedOnPublish()
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var cancellationToken = TestContext.Current.CancellationToken;

        var v1 = ParseModel("CREATE FUNCTION f() RETURNS int DETERMINISTIC RETURN 1;", cancellationToken);
        var v2 = ParseModel("CREATE FUNCTION f() RETURNS int DETERMINISTIC RETURN 2;", cancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await dbModelBuilder.ExtractModelAsync(cancellationToken);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, v1, empty), cancellationToken);

            var published = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            // The body changed, so the function is dropped and recreated — neither engine
            // supports a portable in-place redefinition.
            var comparison = SchemaCompare.Compare(provider, v2, published);
            Assert.IsType<RecreateDelta>(Assert.Single(comparison.Deltas));

            await testDb.PublishAsync(comparison, cancellationToken);

            var republished = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            Assert.True(
                HashUtility.HashesEqual(v2.Hash, republished.Hash),
                $"[{Fixture.EngineName}] The updated function did not round-trip.\n"
                + $"Parsed:    {ModelAssertions.Describe(v2)}\n"
                + $"Extracted: {ModelAssertions.Describe(republished)}");

            var function = Assert.Single(
                republished.Elements, i => i.Type == MariaDbElementTypes.SqlFunction);

            Assert.Contains("RETURN 2", function.GetRequiredProperty<string>(MariaDbPropertyNames.Body));
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task DroppedFunction_IsRemovedOnPublish()
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var cancellationToken = TestContext.Current.CancellationToken;

        var withFunction = ParseModel(
            "CREATE FUNCTION f() RETURNS int DETERMINISTIC RETURN 1;", cancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await dbModelBuilder.ExtractModelAsync(cancellationToken);
            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, withFunction, empty), cancellationToken);

            var published = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(
                    provider, new Model(), published,
                    new DeployOptions { DropObjectsNotInSource = true }),
                cancellationToken);

            var afterDrop = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            Assert.DoesNotContain(
                afterDrop.Elements, i => i.Type == MariaDbElementTypes.SqlFunction);
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class MariaDbFunctionTestsMariaDb(MariaDbFixture fixture)
    : MariaDbFunctionTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbFunctionTestsMySql(MySqlFixture fixture)
    : MariaDbFunctionTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

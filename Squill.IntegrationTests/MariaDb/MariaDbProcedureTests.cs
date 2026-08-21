using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// End-to-end stored procedure tests for the MariaDB provider (issue #41), run against a
/// real MariaDB or MySQL server. Each test parses declarative SQL into a model, publishes
/// it into a fresh database, extracts the database's model, and asserts the two hash-match.
///
/// Running every scenario on both engines is what proves the type normalization: the two
/// report a routine parameter's type differently (MariaDB keeps an integer display width,
/// MySQL does not), so a model that hash-matches on both is one that carries neither
/// engine's spelling.
///
/// The scenarios live on this abstract base and run once per engine via the concrete
/// <c>MariaDb*</c> / <c>MySql*</c> subclasses at the bottom of this file.
/// </summary>
public abstract class MariaDbProcedureTests
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
    public async Task SimpleProcedure_RoundTrips()
    {
        var model = await AssertRoundTripAsync(
            "CREATE PROCEDURE do_nothing() SELECT 1;",
            TestContext.Current.CancellationToken);

        var procedure = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlProcedure);

        Assert.Equal("do_nothing", procedure.Name);
        Assert.Empty(procedure.GetRequiredProperty<string>(MariaDbPropertyNames.Arguments));
    }

    [Fact]
    public async Task ProcedureWithBeginEndBody_RoundTrips()
    {
        // A multi-statement body contains its own semicolons; the deploy path sends each
        // delta as a single command, so it must survive unsplit.
        var model = await AssertRoundTripAsync(
            """
            CREATE TABLE widgets
            (
                id   int NOT NULL PRIMARY KEY,
                name varchar(100) NOT NULL
            );

            CREATE PROCEDURE add_widget(IN widget_id int, IN widget_name varchar(100))
            BEGIN
              INSERT INTO widgets (id, name) VALUES (widget_id, widget_name);
              SELECT COUNT(*) FROM widgets;
            END;
            """,
            TestContext.Current.CancellationToken);

        var procedure = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlProcedure);

        Assert.Equal(
            "IN widget_id int, IN widget_name varchar(100)",
            procedure.GetRequiredProperty<string>(MariaDbPropertyNames.Arguments));

        Assert.Contains(
            "INSERT INTO widgets (id, name) VALUES (widget_id, widget_name);",
            procedure.GetRequiredProperty<string>(MariaDbPropertyNames.Body));
    }

    [Fact]
    public async Task ProcedureReferencingATable_IsCreatedAfterIt()
    {
        // The procedure is declared before the table it writes to. Creating it first would
        // fail, so this proves procedures are ordered last on publish.
        await AssertRoundTripAsync(
            """
            CREATE PROCEDURE add_widget(IN a int)
              INSERT INTO widgets (id) VALUES (a);

            CREATE TABLE widgets (id int NOT NULL PRIMARY KEY);
            """,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ProcedureParameterModes_RoundTrip()
    {
        var model = await AssertRoundTripAsync(
            """
            CREATE PROCEDURE p(IN a int, OUT b varchar(50), INOUT c bigint)
            BEGIN
              SET b = 'x';
              SET c = a;
            END;
            """,
            TestContext.Current.CancellationToken);

        var procedure = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlProcedure);

        Assert.Equal(
            "IN a int, OUT b varchar(50), INOUT c bigint",
            procedure.GetRequiredProperty<string>(MariaDbPropertyNames.Arguments));
    }

    [Theory]
    // The integer types are where the engines disagree: MariaDB reports int(11) and MySQL
    // reports int for the same parameter. Both must produce the same model.
    [InlineData("tinyint", "IN a tinyint")]
    [InlineData("smallint", "IN a smallint")]
    [InlineData("mediumint", "IN a mediumint")]
    [InlineData("int", "IN a int")]
    [InlineData("bigint", "IN a bigint")]
    [InlineData("int unsigned", "IN a int unsigned")]
    [InlineData("bool", "IN a tinyint(1)")]
    [InlineData("decimal(10,2)", "IN a decimal(10,2)")]
    [InlineData("varchar(50)", "IN a varchar(50)")]
    [InlineData("char(3)", "IN a char(3)")]
    [InlineData("text", "IN a text")]
    [InlineData("date", "IN a date")]
    [InlineData("datetime", "IN a datetime")]
    [InlineData("double", "IN a double")]
    public async Task ProcedureParameterTypes_RoundTripIdenticallyOnBothEngines(
        string declared, string expected)
    {
        var model = await AssertRoundTripAsync(
            $"CREATE PROCEDURE p(a {declared}) SELECT 1;",
            TestContext.Current.CancellationToken);

        var procedure = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlProcedure);

        Assert.Equal(expected, procedure.GetRequiredProperty<string>(MariaDbPropertyNames.Arguments));
    }

    [Fact]
    public async Task ProcedureCharacteristics_RoundTrip()
    {
        var model = await AssertRoundTripAsync(
            """
            CREATE PROCEDURE p()
              DETERMINISTIC
              READS SQL DATA
              SQL SECURITY INVOKER
              SELECT 1;
            """,
            TestContext.Current.CancellationToken);

        var procedure = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlProcedure);

        Assert.Equal(true, procedure.GetProperty<bool?>(MariaDbPropertyNames.IsDeterministic));
        Assert.Equal("READS SQL DATA", procedure.GetProperty<string>(MariaDbPropertyNames.SqlDataAccess));
        Assert.Equal(true, procedure.GetProperty<bool?>(MariaDbPropertyNames.IsSecurityInvoker));
    }

    [Fact]
    public async Task DefaultCharacteristics_AreNotStored()
    {
        // Both engines report NOT DETERMINISTIC / CONTAINS SQL / DEFINER for an unadorned
        // procedure. If those were stored, the extracted element would carry facets the
        // parsed one does not and the round trip would fail.
        var model = await AssertRoundTripAsync(
            "CREATE PROCEDURE p() SELECT 1;",
            TestContext.Current.CancellationToken);

        var procedure = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlProcedure);

        Assert.Null(procedure.GetProperty<bool?>(MariaDbPropertyNames.IsDeterministic));
        Assert.Null(procedure.GetProperty<string>(MariaDbPropertyNames.SqlDataAccess));
        Assert.Null(procedure.GetProperty<bool?>(MariaDbPropertyNames.IsSecurityInvoker));
    }

    [Fact]
    public async Task MultipleProcedures_RoundTripInNameOrder()
    {
        var model = await AssertRoundTripAsync(
            """
            CREATE PROCEDURE zeta() SELECT 1;
            CREATE PROCEDURE alpha() SELECT 2;
            CREATE PROCEDURE middle() SELECT 3;
            """,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["alpha", "middle", "zeta"],
            model.Elements
                .Where(i => i.Type == MariaDbElementTypes.SqlProcedure)
                .Select(i => i.Name as string)
                .ToList());
    }

    [Fact]
    public async Task ChangedProcedureBody_IsReplacedOnPublish()
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var cancellationToken = TestContext.Current.CancellationToken;

        var v1 = ParseModel("CREATE PROCEDURE p() SELECT 1;", cancellationToken);
        var v2 = ParseModel("CREATE PROCEDURE p() SELECT 2;", cancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await dbModelBuilder.ExtractModelAsync(cancellationToken);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, v1, empty), cancellationToken);

            var published = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            // The body changed, so the procedure is dropped and recreated — neither engine
            // supports a portable in-place redefinition.
            var comparison = SchemaCompare.Compare(provider, v2, published);
            Assert.IsType<RecreateDelta>(Assert.Single(comparison.Deltas));

            await testDb.PublishAsync(comparison, cancellationToken);

            var republished = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            Assert.True(
                HashUtility.HashesEqual(v2.Hash, republished.Hash),
                $"[{Fixture.EngineName}] The updated procedure did not round-trip.\n"
                + $"Parsed:    {ModelAssertions.Describe(v2)}\n"
                + $"Extracted: {ModelAssertions.Describe(republished)}");

            var procedure = Assert.Single(
                republished.Elements, i => i.Type == MariaDbElementTypes.SqlProcedure);

            Assert.Contains("SELECT 2", procedure.GetRequiredProperty<string>(MariaDbPropertyNames.Body));
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task DroppedProcedure_IsRemovedOnPublish()
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var cancellationToken = TestContext.Current.CancellationToken;

        var withProcedure = ParseModel("CREATE PROCEDURE p() SELECT 1;", cancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await dbModelBuilder.ExtractModelAsync(cancellationToken);
            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, withProcedure, empty), cancellationToken);

            var published = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(
                    provider, new Model(), published,
                    new DeployOptions { DropObjectsNotInSource = true }),
                cancellationToken);

            var afterDrop = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            Assert.DoesNotContain(
                afterDrop.Elements, i => i.Type == MariaDbElementTypes.SqlProcedure);
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    // ---- COMMENT and DEFINER (issue #215) ----

    [Fact]
    public async Task ProcedureComment_RoundTrips()
    {
        // Both engines report ROUTINE_COMMENT verbatim, so a declared comment survives the
        // round trip. assertRedeployNoOp proves it does not re-diff.
        var model = await AssertRoundTripAsync(
            "CREATE PROCEDURE p() COMMENT 'what it does' BEGIN SELECT 1; END;",
            TestContext.Current.CancellationToken);

        var procedure = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlProcedure);

        Assert.Equal("what it does",
            procedure.GetRequiredProperty<string>(MariaDbPropertyNames.Comment));
    }

    [Fact]
    public async Task ProcedureWithoutComment_RoundTrips()
    {
        // The omit-when-default half: an absent comment is reported as the empty string, which
        // must model as no comment or every deploy would see a change.
        var model = await AssertRoundTripAsync(
            "CREATE PROCEDURE p() BEGIN SELECT 1; END;",
            TestContext.Current.CancellationToken);

        var procedure = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlProcedure);

        Assert.Null(procedure.GetProperty<string>(MariaDbPropertyNames.Comment));
    }

    [Fact]
    public async Task ProcedureWithExplicitDefiner_RoundTrips()
    {
        var model = await AssertRoundTripAsync(
            "CREATE DEFINER = 'squill_definer'@'localhost' PROCEDURE p() BEGIN SELECT 1; END;",
            TestContext.Current.CancellationToken);

        var procedure = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlProcedure);

        Assert.Equal("squill_definer@localhost",
            procedure.GetRequiredProperty<string>(MariaDbPropertyNames.Definer));
    }

    [Fact]
    public async Task ProcedureWithoutDefiner_RoundTrips()
    {
        // The catalog always reports a concrete definer, so this is the case that would
        // re-diff forever if an engine-filled definer were modeled as declared.
        var model = await AssertRoundTripAsync(
            "CREATE PROCEDURE p() BEGIN SELECT 1; END;",
            TestContext.Current.CancellationToken);

        var procedure = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlProcedure);

        Assert.Null(procedure.GetProperty<string>(MariaDbPropertyNames.Definer));
    }

}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class MariaDbProcedureTestsMariaDb(MariaDbFixture fixture)
    : MariaDbProcedureTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbProcedureTestsMySql(MySqlFixture fixture)
    : MariaDbProcedureTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

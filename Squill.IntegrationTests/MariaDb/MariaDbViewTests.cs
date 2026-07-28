using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// End-to-end view tests for the MariaDB provider (issue #42), run against a real MariaDB
/// or MySQL server. Each test parses declarative SQL into a model, publishes it into a fresh
/// database, extracts the database's model, and asserts the two hash-match.
///
/// Running every scenario on both engines is what proves the design. A view's query cannot
/// round-trip: each engine rewrites it when it stores it, and not even the same way as the
/// other (MySQL parenthesizes a WHERE clause where MariaDB does not, and both fully qualify
/// every column with the database name). A view is therefore modeled by its name and column
/// list, with the query carried for scripting only — and hashes matching on both engines is
/// what shows those facets really do agree between the two model builders.
///
/// The scenarios live on this abstract base and run once per engine via the concrete
/// <c>MariaDb*</c> / <c>MySql*</c> subclasses at the bottom of this file.
/// </summary>
public abstract class MariaDbViewTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    private const string Users =
        "CREATE TABLE users (id int NOT NULL PRIMARY KEY, name varchar(50), active tinyint(1));";

    private Model ParseModel(string sql, CancellationToken cancellationToken)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser(), (MariaDbFamilyDatabaseSchemaProvider)Fixture.SchemaProvider)
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

    private static IEnumerable<string?> ColumnNames(Element view)
        => view.GetRelationship(MariaDbRelationshipNames.Columns)!
            .Entries.OfType<Element>().Select(i => i.Name);

    [Fact]
    public async Task SimpleView_RoundTrips()
    {
        var model = await AssertRoundTripAsync($"""
            {Users}
            CREATE VIEW active_users AS SELECT id, name FROM users WHERE active = 1;
            """,
            TestContext.Current.CancellationToken);

        var view = Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlView);

        Assert.Equal("active_users", view.Name);
        Assert.Equal(
            new[] { "active_users.id", "active_users.name" }, ColumnNames(view));
    }

    [Fact]
    public async Task ViewWithExplicitColumnList_RoundTrips()
    {
        var model = await AssertRoundTripAsync($"""
            {Users}
            CREATE VIEW user_label (user_id, label) AS SELECT id, name FROM users;
            """,
            TestContext.Current.CancellationToken);

        var view = Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlView);

        Assert.Equal(
            new[] { "user_label.user_id", "user_label.label" }, ColumnNames(view));
    }

    [Fact]
    public async Task ViewWithAliasedExpression_RoundTrips()
    {
        var model = await AssertRoundTripAsync("""
            CREATE TABLE orders (id int NOT NULL PRIMARY KEY, qty int);

            CREATE VIEW order_stock AS SELECT id, qty * 2 AS double_qty FROM orders;
            """,
            TestContext.Current.CancellationToken);

        var view = Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlView);

        Assert.Equal(
            new[] { "order_stock.id", "order_stock.double_qty" }, ColumnNames(view));
    }

    [Fact]
    public async Task ViewOverSelectStar_RoundTrips()
    {
        // SELECT * is expanded against the table's declared columns, so the modeled shape
        // must match what the engine reports back for the deployed view.
        var model = await AssertRoundTripAsync($"""
            {Users}
            CREATE VIEW all_users AS SELECT * FROM users;
            """,
            TestContext.Current.CancellationToken);

        var view = Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlView);

        Assert.Equal(
            new[] { "all_users.id", "all_users.name", "all_users.active" }, ColumnNames(view));
    }

    [Fact]
    public async Task MultipleViews_RoundTrip()
    {
        var model = await AssertRoundTripAsync($"""
            {Users}
            CREATE VIEW b_view AS SELECT id FROM users;
            CREATE VIEW a_view AS SELECT name FROM users;
            """,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, model.Elements.Count(i => i.Type == MariaDbElementTypes.SqlView));
    }

    [Fact]
    public async Task ChangedViewColumns_AreRecreatedOnPublish()
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var cancellationToken = TestContext.Current.CancellationToken;

        var original = ParseModel($"{Users} CREATE VIEW v AS SELECT id, name FROM users;", cancellationToken);
        var updated = ParseModel($"{Users} CREATE VIEW v AS SELECT id, active FROM users;", cancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, original, empty), cancellationToken);

            var deployed = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, updated, deployed), cancellationToken);

            var republished = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            Assert.True(
                HashUtility.HashesEqual(updated.Hash, republished.Hash),
                $"[{Fixture.EngineName}] The redeployed model does not match the updated source.");

            var view = Assert.Single(
                republished.Elements, i => i.Type == MariaDbElementTypes.SqlView);

            Assert.Equal(new[] { "v.id", "v.active" }, ColumnNames(view));
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task DroppedView_IsRemovedOnPublish()
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var cancellationToken = TestContext.Current.CancellationToken;

        var withView = ParseModel($"{Users} CREATE VIEW v AS SELECT id FROM users;", cancellationToken);
        var withoutView = ParseModel(Users, cancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, withView, empty), cancellationToken);

            var deployed = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, withoutView, deployed,
                    new DeployOptions { DropObjectsNotInSource = true }),
                cancellationToken);

            var afterDrop = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            Assert.DoesNotContain(afterDrop.Elements, i => i.Type == MariaDbElementTypes.SqlView);
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class MariaDbViewTestsMariaDb(MariaDbFixture fixture)
    : MariaDbViewTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbViewTestsMySql(MySqlFixture fixture)
    : MariaDbViewTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

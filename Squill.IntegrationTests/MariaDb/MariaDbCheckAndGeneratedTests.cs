using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// End-to-end coverage against real MariaDB and MySQL for issue #120: CHECK constraints
/// (column and table level) and generated (computed) columns. Both were previously dropped
/// silently during the build, with only a non-fatal SQ1002 warning, so the deployed schema
/// was missing something the user wrote.
///
/// Every scenario asserts a redeploy of the same source is a no-op. That is the regression
/// that matters most here: both engines rewrite a CHECK predicate and a generation
/// expression when they store them, so if the declared text were compared against the
/// rewritten text the object would re-diff on every single deploy.
///
/// The scenarios live on this abstract base and run once per engine via the concrete
/// <c>MariaDb*</c> / <c>MySql*</c> subclasses at the bottom of this file.
/// </summary>
public abstract class MariaDbCheckAndGeneratedTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    private async Task<Model> AssertRoundTripAsync(string sql, CancellationToken cancellationToken)
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var model = await WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser(), Fixture.EngineOf()),
            cancellationToken);

        return await RoundTripHarness.AssertRoundTripAsync(
            provider, model, Fixture.EngineName,
            assertRedeployNoOp: true,
            cancellationToken: cancellationToken);
    }

    [Fact]
    public async Task NamedTableLevelCheck_RoundTrips()
    {
        var model = await AssertRoundTripAsync("""
            CREATE TABLE product
            (
                product_id int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                price      int NOT NULL,
                CONSTRAINT ck_price_positive CHECK (price > 0)
            );
            """, TestContext.Current.CancellationToken);

        var check = Assert.Single(model.Elements,
            e => e.Type == MariaDbElementTypes.SqlCheckConstraint);

        Assert.Equal("ck_price_positive", SqlName.UnqualifiedOf((string)check.Name!));
    }

    [Fact]
    public async Task NamedColumnLevelCheck_RoundTrips()
    {
        var model = await AssertRoundTripAsync("""
            CREATE TABLE product
            (
                product_id int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                price      int NOT NULL CONSTRAINT ck_price_positive CHECK (price > 0)
            );
            """, TestContext.Current.CancellationToken);

        var check = Assert.Single(model.Elements,
            e => e.Type == MariaDbElementTypes.SqlCheckConstraint);

        Assert.Equal("ck_price_positive", SqlName.UnqualifiedOf((string)check.Name!));
    }

    /// <summary>
    /// A predicate spanning two columns can only be written at the table level, and is the
    /// case a column-scoped model could not represent.
    /// </summary>
    [Fact]
    public async Task MultiColumnCheck_RoundTrips()
    {
        await AssertRoundTripAsync("""
            CREATE TABLE product
            (
                product_id int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                stock      int NOT NULL,
                reorder_at int NOT NULL,
                CONSTRAINT ck_reorder_below_stock CHECK (reorder_at <= stock)
            );
            """, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MultipleChecksOnOneTable_RoundTrip()
    {
        var model = await AssertRoundTripAsync("""
            CREATE TABLE product
            (
                product_id int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                price      int NOT NULL,
                stock      int NOT NULL,
                CONSTRAINT ck_price_positive CHECK (price > 0),
                CONSTRAINT ck_stock_nonnegative CHECK (stock >= 0)
            );
            """, TestContext.Current.CancellationToken);

        Assert.Equal(2, model.Elements.Count(
            e => e.Type == MariaDbElementTypes.SqlCheckConstraint));
    }

    [Fact]
    public async Task StoredGeneratedColumn_RoundTrips()
    {
        var model = await AssertRoundTripAsync("""
            CREATE TABLE line_item
            (
                line_item_id int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                price        int NOT NULL,
                quantity     int NOT NULL,
                total        int GENERATED ALWAYS AS (price * quantity) STORED
            );
            """, TestContext.Current.CancellationToken);

        Assert.True(GeneratedColumn(model, "total").GetProperty<bool?>(
            MariaDbPropertyNames.IsStored));
    }

    /// <summary>
    /// VIRTUAL is MariaDB's default storage kind and is computed on read rather than stored,
    /// so it must round-trip as a distinct, non-stored generated column.
    /// </summary>
    [Fact]
    public async Task VirtualGeneratedColumn_RoundTrips()
    {
        var model = await AssertRoundTripAsync("""
            CREATE TABLE line_item
            (
                line_item_id int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                price        int NOT NULL,
                doubled      int GENERATED ALWAYS AS (price * 2) VIRTUAL
            );
            """, TestContext.Current.CancellationToken);

        Assert.False(GeneratedColumn(model, "doubled").GetProperty<bool?>(
            MariaDbPropertyNames.IsStored));
    }

    /// <summary>
    /// A CHECK and a generated column on the same table, which is how they most often appear
    /// together and exercises both extraction paths at once.
    /// </summary>
    [Fact]
    public async Task CheckAndGeneratedColumnTogether_RoundTrip()
    {
        await AssertRoundTripAsync("""
            CREATE TABLE line_item
            (
                line_item_id int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                price        int NOT NULL,
                quantity     int NOT NULL,
                total        int GENERATED ALWAYS AS (price * quantity) STORED,
                CONSTRAINT ck_quantity_positive CHECK (quantity > 0)
            );
            """, TestContext.Current.CancellationToken);
    }

    private static Element GeneratedColumn(Model model, string columnName)
        => model.Elements
            .Single(e => e.Type == MariaDbElementTypes.SqlTable)
            .Relationships.Single(r => r.Name == MariaDbRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single(c => SqlName.UnqualifiedOf((string)c.Name!) == columnName);
}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class MariaDbCheckAndGeneratedTestsMariaDb(MariaDbFixture fixture)
    : MariaDbCheckAndGeneratedTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbCheckAndGeneratedTestsMySql(MySqlFixture fixture)
    : MariaDbCheckAndGeneratedTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

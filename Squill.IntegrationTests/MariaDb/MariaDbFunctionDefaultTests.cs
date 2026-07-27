using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// End-to-end coverage against real MariaDB and MySQL for issue #124: current-timestamp
/// column <c>DEFAULT</c>s, as used by Sakila's ubiquitous <c>last_update</c> columns. These
/// were previously dropped during the build with only a non-fatal SQ1002 warning, so the
/// deployed schema was missing a default the user wrote.
///
/// The regression that matters most is the redeploy no-op. The two engines report the very
/// same stored default differently — MySQL as <c>CURRENT_TIMESTAMP</c>, MariaDB as
/// <c>current_timestamp()</c> — and neither preserves which synonym the source used. If the
/// declared spelling were compared against the reported one, the column would re-diff on
/// every single deploy. Running each scenario against both engines is what proves the
/// canonical token holds on both sides.
/// </summary>
public abstract class MariaDbFunctionDefaultTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    private async Task<Model> AssertRoundTripAsync(string sql, CancellationToken cancellationToken)
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var model = await WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser()),
            cancellationToken);

        return await RoundTripHarness.AssertRoundTripAsync(
            provider, model, Fixture.EngineName,
            assertRedeployNoOp: true,
            cancellationToken: cancellationToken);
    }

    [Fact]
    public async Task CurrentTimestampDefault_RoundTrips()
    {
        var model = await AssertRoundTripAsync("""
            CREATE TABLE actor
            (
                actor_id    int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                first_name  varchar(45) NOT NULL,
                last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """, TestContext.Current.CancellationToken);

        Assert.Equal("CURRENT_TIMESTAMP", DefaultOf(model, "last_update"));
    }

    /// <summary>
    /// <c>NOW()</c> is the same default written a different way. Both engines collapse it to
    /// the same stored form, so it must reach the same canonical token — otherwise two
    /// sources that mean the same thing would produce different model hashes.
    /// </summary>
    [Fact]
    public async Task NowSynonymDefault_RoundTripsToSameToken()
    {
        var model = await AssertRoundTripAsync("""
            CREATE TABLE audit_entry
            (
                id         int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                created_at datetime NOT NULL DEFAULT NOW()
            );
            """, TestContext.Current.CancellationToken);

        Assert.Equal("CURRENT_TIMESTAMP", DefaultOf(model, "created_at"));
    }

    /// <summary>
    /// A current-timestamp default alongside the constant defaults that were already modeled,
    /// mirroring the shape of a real Sakila table.
    /// </summary>
    [Fact]
    public async Task MixedConstantAndFunctionDefaults_RoundTrip()
    {
        var model = await AssertRoundTripAsync("""
            CREATE TABLE staff
            (
                staff_id    int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                active      boolean NOT NULL DEFAULT true,
                archived    boolean NOT NULL DEFAULT false,
                rental_rate decimal(4, 2) NOT NULL DEFAULT 4.99,
                status      varchar(20) NOT NULL DEFAULT 'active',
                last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """, TestContext.Current.CancellationToken);

        Assert.Equal("CURRENT_TIMESTAMP", DefaultOf(model, "last_update"));
        Assert.Equal("'active'", DefaultOf(model, "status"));
        Assert.Equal("4.99", DefaultOf(model, "rental_rate"));

        // boolean is tinyint(1) on both engines, so a true/false default comes back as 1/0.
        Assert.Equal("1", DefaultOf(model, "active"));
        Assert.Equal("0", DefaultOf(model, "archived"));
    }

    /// <summary>
    /// The exact shape of every Sakila <c>last_update</c> column. <c>ON UPDATE
    /// CURRENT_TIMESTAMP</c> shares a grammar production with the default itself, so before
    /// issue #124 the two ran together into one unrecognizable token and *both* halves went
    /// unmodeled. Both engines report the clause in <c>EXTRA</c>, spelled differently
    /// (<c>on update CURRENT_TIMESTAMP</c> vs <c>on update current_timestamp()</c>), which is
    /// why this runs against both.
    /// </summary>
    [Fact]
    public async Task DefaultWithOnUpdate_RoundTrips()
    {
        var model = await AssertRoundTripAsync("""
            CREATE TABLE actor
            (
                actor_id    int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                first_name  varchar(45) NOT NULL,
                last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
            );
            """, TestContext.Current.CancellationToken);

        var lastUpdate = Column(model, "last_update");
        Assert.Equal("CURRENT_TIMESTAMP", lastUpdate.GetProperty<string>(MariaDbPropertyNames.DefaultValue));
        Assert.True(lastUpdate.GetProperty<bool?>(MariaDbPropertyNames.OnUpdateCurrentTimestamp));
    }

    /// <summary>
    /// A plain current-timestamp default must not acquire the auto-refresh clause: the two are
    /// independent, and scripting ON UPDATE onto a column that did not declare it would change
    /// the table's behavior.
    /// </summary>
    [Fact]
    public async Task DefaultWithoutOnUpdate_DoesNotGainTheClause()
    {
        var model = await AssertRoundTripAsync("""
            CREATE TABLE plain_stamp
            (
                id       int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                stamped  timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            """, TestContext.Current.CancellationToken);

        Assert.Null(Column(model, "stamped")
            .GetProperty<bool?>(MariaDbPropertyNames.OnUpdateCurrentTimestamp));
    }

    /// <summary>
    /// A column that merely has a current-timestamp default must not be mistaken for a
    /// generated column. MySQL reports <c>DEFAULT_GENERATED</c> in <c>EXTRA</c> for exactly
    /// such a column — a string that contains "GENERATED" without being either of the
    /// <c>STORED GENERATED</c> / <c>VIRTUAL GENERATED</c> forms that mark a real generated
    /// column — so a loose match gave the extracted column generation properties the parsed
    /// model does not have, breaking the round trip on MySQL only.
    /// </summary>
    [Fact]
    public async Task CurrentTimestampColumn_IsNotTreatedAsGenerated()
    {
        var model = await AssertRoundTripAsync("""
            CREATE TABLE reading
            (
                id          int NOT NULL AUTO_INCREMENT PRIMARY KEY,
                taken_at    timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
                celsius     int NOT NULL,
                fahrenheit  int GENERATED ALWAYS AS (celsius * 9 / 5 + 32) STORED
            );
            """, TestContext.Current.CancellationToken);

        var takenAt = Column(model, "taken_at");
        Assert.Equal("CURRENT_TIMESTAMP", takenAt.GetProperty<string>(MariaDbPropertyNames.DefaultValue));
        Assert.Null(takenAt.GetProperty<string>(MariaDbPropertyNames.GeneratedExpression));
        Assert.Null(takenAt.GetProperty<bool?>(MariaDbPropertyNames.IsStored));

        // The genuinely generated column alongside it is still recognized as one.
        var fahrenheit = Column(model, "fahrenheit");
        Assert.NotNull(fahrenheit.GetProperty<string>(MariaDbPropertyNames.GeneratedExpression));
        Assert.True(fahrenheit.GetProperty<bool?>(MariaDbPropertyNames.IsStored));
    }

    private static Element Column(Model model, string columnName)
        => model.Elements
            .Single(e => e.Type == MariaDbElementTypes.SqlTable)
            .Relationships.Single(r => r.Name == MariaDbRelationshipNames.Columns)
            .Entries.OfType<Element>()
            .Single(c => SqlName.UnqualifiedOf((string)c.Name!) == columnName);

    private static string? DefaultOf(Model model, string columnName)
        => Column(model, columnName).GetProperty<string>(MariaDbPropertyNames.DefaultValue);
}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class MariaDbFunctionDefaultTestsMariaDb(MariaDbFixture fixture)
    : MariaDbFunctionDefaultTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbFunctionDefaultTestsMySql(MySqlFixture fixture)
    : MariaDbFunctionDefaultTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

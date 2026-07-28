using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// End-to-end scheduled-event tests for the MariaDB provider (issue #122), run against a real
/// MariaDB or MySQL server. Each test parses declarative SQL into a model, publishes it into a
/// fresh database, extracts the database's model, and asserts the two hash-match — proving the
/// parsed and extracted representations agree on an event's schedule, status and verbatim
/// body. Mirrors <see cref="MariaDbTriggerTests"/>.
///
/// These tests never wait for an event to actually fire: the event scheduler is off by default
/// on MariaDB (and on by default on MySQL), so firing is not something a schema test can rely
/// on. What is asserted is that the declared event exists with the declared schedule and that
/// redeploying it is a no-op.
///
/// The scenarios live on this abstract base and run once per engine via the concrete
/// <c>MariaDb*</c> / <c>MySql*</c> subclasses at the bottom of this file.
/// </summary>
public abstract class MariaDbEventTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    private Model ParseModel(string sql, CancellationToken cancellationToken)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser(), Fixture.EngineOf())
            .ExtractModelAsync(cancellationToken).GetAwaiter().GetResult().Model;
    }

    // Parses the given SQL, publishes it into a fresh database, and asserts the re-extracted
    // model hash-matches the parsed one. assertRedeployNoOp proves the event converges: a
    // second deploy of the same source must produce no deltas at all.
    private async Task<Model> AssertRoundTripAsync(string sql, CancellationToken cancellationToken)
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var model = ParseModel(sql, cancellationToken);
        return await RoundTripHarness.AssertRoundTripAsync(
            provider, model, Fixture.EngineName, assertRedeployNoOp: true,
            cancellationToken: cancellationToken);
    }

    // A table the event bodies write to.
    private const string Tables =
        """
        CREATE TABLE stats (
          id INT NOT NULL PRIMARY KEY,
          n INT NOT NULL
        );
        """;

    [Fact]
    public async Task OneTimeEvent_RoundTrips()
    {
        var model = await AssertRoundTripAsync(
            Tables
            + """

            CREATE EVENT seed_stats ON SCHEDULE AT '2030-01-01 00:00:00'
            DO INSERT INTO stats (id, n) VALUES (1, 1);
            """,
            TestContext.Current.CancellationToken);

        var element = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlEvent);

        Assert.Equal("seed_stats", element.Name);
        Assert.Equal("ONE TIME", element.GetRequiredProperty<string>(MariaDbPropertyNames.EventType));
        Assert.Equal(
            "2030-01-01 00:00:00",
            element.GetRequiredProperty<string>(MariaDbPropertyNames.ExecuteAt));
    }

    [Fact]
    public async Task RecurringEvent_RoundTrips()
    {
        var model = await AssertRoundTripAsync(
            Tables
            + """

            CREATE EVENT rollup_stats ON SCHEDULE EVERY 1 DAY STARTS '2030-01-01 00:00:00'
            DO UPDATE stats SET n = n + 1 WHERE id = 1;
            """,
            TestContext.Current.CancellationToken);

        var element = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlEvent);

        Assert.Equal("RECURRING", element.GetRequiredProperty<string>(MariaDbPropertyNames.EventType));
        Assert.Equal("1", element.GetRequiredProperty<string>(MariaDbPropertyNames.IntervalValue));
        Assert.Equal("DAY", element.GetRequiredProperty<string>(MariaDbPropertyNames.IntervalField));
        Assert.Equal(
            "2030-01-01 00:00:00",
            element.GetRequiredProperty<string>(MariaDbPropertyNames.Starts));
    }

    [Fact]
    public async Task RecurringEventWithEnds_RoundTrips()
    {
        var model = await AssertRoundTripAsync(
            Tables
            + """

            CREATE EVENT windowed ON SCHEDULE EVERY 2 HOUR
            STARTS '2030-01-01 00:00:00' ENDS '2031-01-01 00:00:00'
            ON COMPLETION PRESERVE
            DO UPDATE stats SET n = n + 1 WHERE id = 1;
            """,
            TestContext.Current.CancellationToken);

        var element = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlEvent);

        Assert.Equal(
            "2031-01-01 00:00:00",
            element.GetRequiredProperty<string>(MariaDbPropertyNames.Ends));
        Assert.True(
            element.GetRequiredProperty<bool>(MariaDbPropertyNames.PreserveOnCompletion));
    }

    [Fact]
    public async Task CompoundIntervalEvent_RoundTrips()
    {
        // EVERY '2:3' DAY_HOUR: the catalog reports the value space-separated, and the
        // generator must write it back colon-separated for the CREATE to parse.
        var model = await AssertRoundTripAsync(
            Tables
            + """

            CREATE EVENT compound ON SCHEDULE EVERY '2:3' DAY_HOUR
            STARTS '2030-01-01 00:00:00'
            DO UPDATE stats SET n = n + 1 WHERE id = 1;
            """,
            TestContext.Current.CancellationToken);

        var element = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlEvent);

        Assert.Equal("2 3", element.GetRequiredProperty<string>(MariaDbPropertyNames.IntervalValue));
        Assert.Equal("DAY_HOUR", element.GetRequiredProperty<string>(MariaDbPropertyNames.IntervalField));
    }

    [Fact]
    public async Task DisabledEventWithComment_RoundTrips()
    {
        var model = await AssertRoundTripAsync(
            Tables
            + """

            CREATE EVENT paused ON SCHEDULE AT '2030-01-01 00:00:00'
            ON COMPLETION PRESERVE DISABLE COMMENT 'paused until launch'
            DO INSERT INTO stats (id, n) VALUES (2, 1);
            """,
            TestContext.Current.CancellationToken);

        var element = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlEvent);

        Assert.Equal("DISABLED", element.GetRequiredProperty<string>(MariaDbPropertyNames.Status));
        Assert.Equal(
            "paused until launch",
            element.GetRequiredProperty<string>(MariaDbPropertyNames.Comment));
    }

    [Fact]
    public async Task DisableOnSlaveEvent_RoundTrips()
    {
        // MariaDB reports this status as SLAVESIDE_DISABLED and MySQL as
        // REPLICA_SIDE_DISABLED; the extractor normalizes MySQL's onto the MariaDB spelling so
        // one declaration round-trips on both engines. This test running green on both is what
        // proves that normalization works.
        var model = await AssertRoundTripAsync(
            Tables
            + """

            CREATE EVENT replica_only ON SCHEDULE AT '2030-01-01 00:00:00'
            ON COMPLETION PRESERVE DISABLE ON SLAVE
            DO INSERT INTO stats (id, n) VALUES (3, 1);
            """,
            TestContext.Current.CancellationToken);

        var element = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlEvent);

        Assert.Equal(
            "SLAVESIDE_DISABLED",
            element.GetRequiredProperty<string>(MariaDbPropertyNames.Status));
    }

    [Fact]
    public async Task BeginEndEventBody_RoundTrips()
    {
        var model = await AssertRoundTripAsync(
            Tables
            + """

            CREATE EVENT multi ON SCHEDULE EVERY 1 WEEK STARTS '2030-01-01 00:00:00'
            DO
            BEGIN
              INSERT INTO stats (id, n) VALUES (4, 1);
              UPDATE stats SET n = n + 1 WHERE id = 4;
            END;
            """,
            TestContext.Current.CancellationToken);

        var element = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlEvent);

        var body = element.GetRequiredProperty<string>(MariaDbPropertyNames.Body);
        Assert.StartsWith("BEGIN", body);
        Assert.EndsWith("END", body);
    }

    [Fact]
    public async Task MultipleEvents_RoundTrip()
    {
        // Ordering matters: the extractor reads events by name and the Merkle hash is
        // order-sensitive, so declaring them out of order must still round-trip.
        var model = await AssertRoundTripAsync(
            Tables
            + """

            CREATE EVENT zeta ON SCHEDULE AT '2030-01-01 00:00:00'
            DO INSERT INTO stats (id, n) VALUES (5, 1);
            CREATE EVENT alpha ON SCHEDULE EVERY 1 DAY STARTS '2030-01-01 00:00:00'
            DO UPDATE stats SET n = n + 1 WHERE id = 5;
            """,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            2, model.Elements.Count(i => i.Type == MariaDbElementTypes.SqlEvent));
    }

    [Fact]
    public async Task EventCoexistsWithTrigger_RoundTrips()
    {
        // An event and a trigger are distinct element types that both sort to the end of the
        // model. This pins the relative order the two builders must agree on.
        await AssertRoundTripAsync(
            Tables
            + """

            CREATE TRIGGER bump BEFORE INSERT ON stats
            FOR EACH ROW
            SET NEW.n = NEW.n + 1;
            CREATE EVENT rollup ON SCHEDULE EVERY 1 DAY STARTS '2030-01-01 00:00:00'
            DO UPDATE stats SET n = n + 1 WHERE id = 1;
            """,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ChangedEventSchedule_IsReplacedOnPublish()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);

        var v1 = ParseModel(
            Tables
            + """

            CREATE EVENT rollup ON SCHEDULE EVERY 1 DAY STARTS '2030-01-01 00:00:00'
            DO UPDATE stats SET n = n + 1 WHERE id = 1;
            """,
            cancellationToken);

        var v2 = ParseModel(
            Tables
            + """

            CREATE EVENT rollup ON SCHEDULE EVERY 6 HOUR STARTS '2030-01-01 00:00:00'
            DO UPDATE stats SET n = n + 2 WHERE id = 1;
            """,
            cancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await dbModelBuilder.ExtractModelAsync(cancellationToken);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, v1, empty), cancellationToken);

            var published = await dbModelBuilder.ExtractModelAsync(cancellationToken);
            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, v2, published), cancellationToken);

            var republished = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            var element = Assert.Single(
                republished.Elements, i => i.Type == MariaDbElementTypes.SqlEvent);

            Assert.Equal("6", element.GetRequiredProperty<string>(MariaDbPropertyNames.IntervalValue));
            Assert.Equal("HOUR", element.GetRequiredProperty<string>(MariaDbPropertyNames.IntervalField));

            // The redeployed event matches the new declaration exactly, so a further deploy
            // produces no deltas at all.
            Assert.Empty(SchemaCompare.Compare(provider, v2, republished).Deltas);
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task DroppedEvent_IsRemovedOnPublish()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);

        var withEvent = ParseModel(
            Tables
            + """

            CREATE EVENT rollup ON SCHEDULE EVERY 1 DAY STARTS '2030-01-01 00:00:00'
            DO UPDATE stats SET n = n + 1 WHERE id = 1;
            """,
            cancellationToken);

        var withoutEvent = ParseModel(Tables, cancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await dbModelBuilder.ExtractModelAsync(cancellationToken);
            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, withEvent, empty), cancellationToken);

            var published = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(
                    provider, withoutEvent, published,
                    new DeployOptions { DropObjectsNotInSource = true }),
                cancellationToken);

            var afterDrop = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            Assert.DoesNotContain(
                afterDrop.Elements, i => i.Type == MariaDbElementTypes.SqlEvent);
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }
}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class MariaDbEventTestsMariaDb(MariaDbFixture fixture)
    : MariaDbEventTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbEventTestsMySql(MySqlFixture fixture)
    : MariaDbEventTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

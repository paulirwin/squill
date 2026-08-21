using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// End-to-end trigger tests for the MariaDB provider (issue #100), run against a real MariaDB
/// or MySQL server. Each test parses declarative SQL into a model, publishes it into a fresh
/// database, extracts the database's model, and asserts the two hash-match — proving the
/// parsed and extracted representations agree on a trigger's timing, event, target table and
/// verbatim body. Mirrors <see cref="MariaDbFunctionTests"/>.
///
/// The scenarios live on this abstract base and run once per engine via the concrete
/// <c>MariaDb*</c> / <c>MySql*</c> subclasses at the bottom of this file.
/// </summary>
public abstract class MariaDbTriggerTests
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

    // The two Sakila-style tables the trigger scenarios fire on and write to.
    private const string Tables =
        """
        CREATE TABLE film (
          film_id INT NOT NULL PRIMARY KEY,
          title VARCHAR(255) NOT NULL,
          description TEXT
        );
        CREATE TABLE film_text (
          film_id INT NOT NULL PRIMARY KEY,
          title VARCHAR(255) NOT NULL,
          description TEXT
        );
        """;

    [Fact]
    public async Task AfterInsertTrigger_RoundTrips()
    {
        var model = await AssertRoundTripAsync(
            Tables
            + """

            CREATE TRIGGER ins_film AFTER INSERT ON film
            FOR EACH ROW
            BEGIN
              INSERT INTO film_text (film_id, title, description)
              VALUES (NEW.film_id, NEW.title, NEW.description);
            END;
            """,
            TestContext.Current.CancellationToken);

        var trigger = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlTrigger);

        Assert.Equal("film.ins_film", trigger.Name);
        Assert.Equal("ins_film", trigger.GetRequiredProperty<string>(MariaDbPropertyNames.RoutineName));
        Assert.Equal("AFTER", trigger.GetRequiredProperty<string>(MariaDbPropertyNames.Timing));
        Assert.Equal("INSERT", trigger.GetRequiredProperty<string>(MariaDbPropertyNames.Event));
    }

    [Fact]
    public async Task AfterDeleteTrigger_RoundTrips()
    {
        var model = await AssertRoundTripAsync(
            Tables
            + """

            CREATE TRIGGER del_film AFTER DELETE ON film
            FOR EACH ROW
            BEGIN
              DELETE FROM film_text WHERE film_id = OLD.film_id;
            END;
            """,
            TestContext.Current.CancellationToken);

        var trigger = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlTrigger);

        Assert.Equal("DELETE", trigger.GetRequiredProperty<string>(MariaDbPropertyNames.Event));
    }

    [Fact]
    public async Task BeforeUpdateTrigger_RoundTrips()
    {
        var model = await AssertRoundTripAsync(
            Tables
            + """

            CREATE TRIGGER upd_film BEFORE UPDATE ON film
            FOR EACH ROW
            BEGIN
              SET NEW.title = UPPER(NEW.title);
            END;
            """,
            TestContext.Current.CancellationToken);

        var trigger = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlTrigger);

        Assert.Equal("BEFORE", trigger.GetRequiredProperty<string>(MariaDbPropertyNames.Timing));
        Assert.Equal("UPDATE", trigger.GetRequiredProperty<string>(MariaDbPropertyNames.Event));
    }

    [Fact]
    public async Task MultipleTriggers_RoundTripInNameOrder()
    {
        // Triggers are extracted ordered by name; a model with several must adopt that order to
        // hash-match. This mirrors the Sakila del_film / ins_film / upd_film trio.
        var model = await AssertRoundTripAsync(
            Tables
            + """

            CREATE TRIGGER upd_film AFTER UPDATE ON film
            FOR EACH ROW
            BEGIN
              UPDATE film_text SET title = NEW.title WHERE film_id = OLD.film_id;
            END;

            CREATE TRIGGER ins_film AFTER INSERT ON film
            FOR EACH ROW
            BEGIN
              INSERT INTO film_text (film_id, title) VALUES (NEW.film_id, NEW.title);
            END;

            CREATE TRIGGER del_film AFTER DELETE ON film
            FOR EACH ROW
            BEGIN
              DELETE FROM film_text WHERE film_id = OLD.film_id;
            END;
            """,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["del_film", "ins_film", "upd_film"],
            model.Elements
                .Where(i => i.Type == MariaDbElementTypes.SqlTrigger)
                .Select(i => i.GetRequiredProperty<string>(MariaDbPropertyNames.RoutineName))
                .ToList());
    }

    [Fact]
    public async Task TriggerFires_AfterDeploy()
    {
        // Beyond hash-matching, the emitted DDL must be executable and actually fire: inserting
        // into film should populate film_text via the ins_film trigger.
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var cancellationToken = TestContext.Current.CancellationToken;

        var model = ParseModel(
            Tables
            + """

            CREATE TRIGGER ins_film AFTER INSERT ON film
            FOR EACH ROW
            BEGIN
              INSERT INTO film_text (film_id, title, description)
              VALUES (NEW.film_id, NEW.title, NEW.description);
            END;
            """,
            cancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await dbModelBuilder.ExtractModelAsync(cancellationToken);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, model, empty), cancellationToken);

            await testDb.ConnectAsync(cancellationToken);

            await testDb.RunScriptAsync(
                "INSERT INTO film (film_id, title, description) "
                + "VALUES (1, 'Inception', 'A dream within a dream');",
                cancellationToken: cancellationToken);

            // The ins_film trigger copied the row into film_text.
            await using var reader = await testDb.RunScriptReaderAsync(
                "SELECT title FROM film_text WHERE film_id = 1;",
                cancellationToken: cancellationToken);

            Assert.True(await reader.ReadAsync(cancellationToken),
                $"[{Fixture.EngineName}] the ins_film trigger did not populate film_text.");
            Assert.Equal("Inception", reader.GetString(0));
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task ChangedTriggerBody_IsReplacedOnPublish()
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var cancellationToken = TestContext.Current.CancellationToken;

        var v1 = ParseModel(
            Tables
            + "\nCREATE TRIGGER ins_film AFTER INSERT ON film FOR EACH ROW "
            + "INSERT INTO film_text (film_id, title) VALUES (NEW.film_id, NEW.title);",
            cancellationToken);
        var v2 = ParseModel(
            Tables
            + "\nCREATE TRIGGER ins_film AFTER INSERT ON film FOR EACH ROW "
            + "INSERT INTO film_text (film_id, title) VALUES (NEW.film_id, UPPER(NEW.title));",
            cancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await dbModelBuilder.ExtractModelAsync(cancellationToken);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, v1, empty), cancellationToken);

            var published = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            // The body changed, so the trigger is dropped and recreated — neither engine
            // supports a portable in-place redefinition.
            var comparison = SchemaCompare.Compare(provider, v2, published);
            Assert.IsType<RecreateDelta>(Assert.Single(comparison.Deltas));

            await testDb.PublishAsync(comparison, cancellationToken);

            var republished = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            Assert.True(
                HashUtility.HashesEqual(v2.Hash, republished.Hash),
                $"[{Fixture.EngineName}] The updated trigger did not round-trip.\n"
                + $"Parsed:    {ModelAssertions.Describe(v2)}\n"
                + $"Extracted: {ModelAssertions.Describe(republished)}");

            var trigger = Assert.Single(
                republished.Elements, i => i.Type == MariaDbElementTypes.SqlTrigger);

            Assert.Contains("UPPER", trigger.GetRequiredProperty<string>(MariaDbPropertyNames.Body));
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task DroppedTrigger_IsRemovedOnPublish()
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var cancellationToken = TestContext.Current.CancellationToken;

        var withTrigger = ParseModel(
            Tables
            + "\nCREATE TRIGGER ins_film AFTER INSERT ON film FOR EACH ROW "
            + "INSERT INTO film_text (film_id, title) VALUES (NEW.film_id, NEW.title);",
            cancellationToken);
        var withoutTrigger = ParseModel(Tables, cancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await dbModelBuilder.ExtractModelAsync(cancellationToken);
            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, withTrigger, empty), cancellationToken);

            var published = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(
                    provider, withoutTrigger, published,
                    new DeployOptions { DropObjectsNotInSource = true }),
                cancellationToken);

            var afterDrop = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            Assert.DoesNotContain(
                afterDrop.Elements, i => i.Type == MariaDbElementTypes.SqlTrigger);
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    // ---- Firing order and DEFINER (issue #215) ----

    [Fact]
    public async Task TriggerOrdering_RoundTrips()
    {
        // The declared order is the reverse of the name order, so nothing but a real FOLLOWS /
        // PRECEDES could produce it. assertRedeployNoOp proves the position round-trips: if the
        // parsed and extracted models disagreed about ACTION_ORDER, the redeploy would script a
        // change.
        var model = await AssertRoundTripAsync(
            Tables
            + """
            CREATE TRIGGER a_trig BEFORE INSERT ON film
            FOR EACH ROW SET NEW.description = CONCAT(COALESCE(NEW.description, ''), 'A');
            CREATE TRIGGER z_trig BEFORE INSERT ON film
            FOR EACH ROW PRECEDES a_trig
            SET NEW.description = CONCAT(COALESCE(NEW.description, ''), 'Z');
            """,
            TestContext.Current.CancellationToken);

        var triggers = model.Elements
            .Where(i => i.Type == MariaDbElementTypes.SqlTrigger)
            .ToList();

        Assert.Equal(2, triggers.Count);

        Assert.Equal(1, triggers
            .Single(i => (string)i.Name! == "film.z_trig")
            .GetProperty<int?>(MariaDbPropertyNames.ActionOrder));
        Assert.Equal(2, triggers
            .Single(i => (string)i.Name! == "film.a_trig")
            .GetProperty<int?>(MariaDbPropertyNames.ActionOrder));
    }

    [Fact]
    public async Task DeclaredTriggerOrder_IsTheOrderTheyFire()
    {
        // The point of the feature: the deployed triggers must actually run in the declared
        // order. Each appends a letter, so the stored value spells out the firing sequence.
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var cancellationToken = TestContext.Current.CancellationToken;

        var model = ParseModel(
            Tables
            + """
            CREATE TRIGGER a_trig BEFORE INSERT ON film
            FOR EACH ROW SET NEW.description = CONCAT(COALESCE(NEW.description, ''), 'A');
            CREATE TRIGGER z_trig BEFORE INSERT ON film
            FOR EACH ROW PRECEDES a_trig
            SET NEW.description = CONCAT(COALESCE(NEW.description, ''), 'Z');
            """,
            cancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await dbModelBuilder.ExtractModelAsync(cancellationToken);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, model, empty), cancellationToken);

            await testDb.ConnectAsync(cancellationToken);

            await testDb.RunScriptAsync(
                "INSERT INTO film (film_id, title) VALUES (1, 'Inception');",
                cancellationToken: cancellationToken);

            await using var reader = await testDb.RunScriptReaderAsync(
                "SELECT description FROM film WHERE film_id = 1;",
                cancellationToken: cancellationToken);

            Assert.True(await reader.ReadAsync(cancellationToken),
                $"[{Fixture.EngineName}] the inserted film row was not found.");

            // z_trig PRECEDES a_trig, so Z is appended first even though a_trig is declared
            // first and sorts first by name.
            Assert.Equal("ZA", reader.GetString(0));
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task TriggerAddedToAnExistingGroup_LandsInTheDeclaredPosition()
    {
        // The case a bare creation order cannot handle: the group already exists on the server,
        // so the new trigger has to be placed with an explicit clause rather than by being
        // created at the right moment.
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var cancellationToken = TestContext.Current.CancellationToken;

        var v1 = ParseModel(
            Tables
            + """
            CREATE TRIGGER m_trig BEFORE INSERT ON film
            FOR EACH ROW SET NEW.description = CONCAT(COALESCE(NEW.description, ''), 'M');
            """,
            cancellationToken);

        // b_trig is added ahead of the m_trig already on the server, and must fire first.
        var v2 = ParseModel(
            Tables
            + """
            CREATE TRIGGER b_trig BEFORE INSERT ON film
            FOR EACH ROW SET NEW.description = CONCAT(COALESCE(NEW.description, ''), 'B');
            CREATE TRIGGER m_trig BEFORE INSERT ON film
            FOR EACH ROW FOLLOWS b_trig
            SET NEW.description = CONCAT(COALESCE(NEW.description, ''), 'M');
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

            await testDb.ConnectAsync(cancellationToken);

            await testDb.RunScriptAsync(
                "INSERT INTO film (film_id, title) VALUES (1, 'Inception');",
                cancellationToken: cancellationToken);

            await using var reader = await testDb.RunScriptReaderAsync(
                "SELECT description FROM film WHERE film_id = 1;",
                cancellationToken: cancellationToken);

            Assert.True(await reader.ReadAsync(cancellationToken),
                $"[{Fixture.EngineName}] the inserted film row was not found.");
            Assert.Equal("BM", reader.GetString(0));
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

    [Fact]
    public async Task TriggerWithoutDefiner_RoundTrips()
    {
        // Both engines report a concrete DEFINER even for a trigger created without the clause,
        // so this is the case that would re-diff forever if an extracted definer were modeled
        // as declared. assertRedeployNoOp is the whole assertion.
        var model = await AssertRoundTripAsync(
            Tables
            + "CREATE TRIGGER ins_film BEFORE INSERT ON film "
            + "FOR EACH ROW SET NEW.description = 'set';",
            TestContext.Current.CancellationToken);

        var trigger = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlTrigger);

        Assert.Null(trigger.GetProperty<string>(MariaDbPropertyNames.Definer));
    }

    [Fact]
    public async Task TriggerWithCurrentUserDefiner_RoundTrips()
    {
        // DEFINER = CURRENT_USER resolves to the deploying user, which is indistinguishable in
        // the catalog from declaring nothing, so it models as no definer and must not re-diff.
        var model = await AssertRoundTripAsync(
            Tables
            + "CREATE DEFINER = CURRENT_USER TRIGGER ins_film BEFORE INSERT ON film "
            + "FOR EACH ROW SET NEW.description = 'set';",
            TestContext.Current.CancellationToken);

        var trigger = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlTrigger);

        Assert.Null(trigger.GetProperty<string>(MariaDbPropertyNames.Definer));
    }

    [Fact]
    public async Task TriggerWithExplicitDefiner_RoundTrips()
    {
        // An account other than the deploying user is the case that actually stores a Definer
        // property, so this is what proves the modeled value matches what the catalog reports.
        // The account need not exist for the trigger to be created (measured on both engines).
        var model = await AssertRoundTripAsync(
            Tables
            + "CREATE DEFINER = 'squill_definer'@'localhost' TRIGGER ins_film "
            + "BEFORE INSERT ON film FOR EACH ROW SET NEW.description = 'set';",
            TestContext.Current.CancellationToken);

        var trigger = Assert.Single(
            model.Elements, i => i.Type == MariaDbElementTypes.SqlTrigger);

        Assert.Equal("squill_definer@localhost",
            trigger.GetRequiredProperty<string>(MariaDbPropertyNames.Definer));
    }

    [Fact]
    public async Task TriggerFollowingOneDeclaredLater_RoundTripsAndFiresInOrder()
    {
        // Declaration order must not matter in the source, but the generated script still has
        // to create the target first, since both engines reject a FOLLOWS naming a trigger that
        // does not exist yet. This proves the two coexist against a real server.
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var cancellationToken = TestContext.Current.CancellationToken;

        var model = ParseModel(
            Tables
            + """
            CREATE TRIGGER b_trig BEFORE INSERT ON film
            FOR EACH ROW FOLLOWS a_trig
            SET NEW.description = CONCAT(COALESCE(NEW.description, ''), 'B');
            CREATE TRIGGER a_trig BEFORE INSERT ON film
            FOR EACH ROW SET NEW.description = CONCAT(COALESCE(NEW.description, ''), 'A');
            """,
            cancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var empty = await dbModelBuilder.ExtractModelAsync(cancellationToken);
            await testDb.PublishAsync(SchemaCompare.Compare(provider, model, empty), cancellationToken);

            await testDb.ConnectAsync(cancellationToken);

            await testDb.RunScriptAsync(
                "INSERT INTO film (film_id, title) VALUES (1, 'Inception');",
                cancellationToken: cancellationToken);

            await using var reader = await testDb.RunScriptReaderAsync(
                "SELECT description FROM film WHERE film_id = 1;",
                cancellationToken: cancellationToken);

            Assert.True(await reader.ReadAsync(cancellationToken),
                $"[{Fixture.EngineName}] the inserted film row was not found.");
            Assert.Equal("AB", reader.GetString(0));
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
    }

}

// ---- Per-engine bindings: each scenario runs once against MariaDB and once against MySQL. ----

public sealed class MariaDbTriggerTestsMariaDb(MariaDbFixture fixture)
    : MariaDbTriggerTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbTriggerTestsMySql(MySqlFixture fixture)
    : MariaDbTriggerTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

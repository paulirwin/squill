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

    private static Model ParseModel(string sql, CancellationToken cancellationToken)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser())
            .ExtractModelAsync(cancellationToken).GetAwaiter().GetResult().Model;
    }

    // Parses the given SQL, publishes it into a fresh database, and asserts the re-extracted
    // model hash-matches the parsed one. Returns the extracted model for further assertions.
    private async Task<Model> AssertRoundTripAsync(string sql, CancellationToken cancellationToken)
    {
        var provider = new MariaDbDatabaseProvider(Fixture.ConnectionString);
        var model = ParseModel(sql, cancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var targetModel = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, model, targetModel), cancellationToken);

            var newModel = await dbModelBuilder.ExtractModelAsync(cancellationToken);

            Assert.True(
                HashUtility.HashesEqual(model.Hash, newModel.Hash),
                $"[{Fixture.EngineName}] Parsed and extracted model hashes do not match.\n"
                + $"Parsed:    {Describe(model)}\n"
                + $"Extracted: {Describe(newModel)}");

            // Redeploying the same source must be a no-op: if any facet of a trigger did not
            // round-trip, the comparison would produce a spurious delta here.
            Assert.Empty(SchemaCompare.Compare(provider, model, newModel).Deltas);

            return newModel;
        }
        finally
        {
            await testDb.DropAsync(cancellationToken);
        }
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
                + $"Parsed:    {Describe(v2)}\n"
                + $"Extracted: {Describe(republished)}");

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

    // Element type and name in order, so an ordering mismatch reports what actually differs
    // rather than just "hashes do not match".
    private static string Describe(Model model)
        => string.Join(" | ", model.Elements.Select(i => $"{i.Type}:{i.Name}"));
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

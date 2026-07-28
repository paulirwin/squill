using Squill.Core;
using Squill.TestFramework;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Tests that CREATE TRIGGER maps to the expected model element (issue #100). The trigger's
/// name is folded with the table it fires on (table.trigger) so same-named triggers on
/// different tables stay distinct, while its timing, event and verbatim body are recorded as
/// properties — the facets that let a parsed model hash-match one extracted from either engine.
/// </summary>
public class CreateTriggerModelTests
{
    // A minimal table the trigger fires on and one it writes to, so the workspace validates.
    private const string Tables =
        "CREATE TABLE film (film_id INT PRIMARY KEY, title VARCHAR(50));\n"
        + "CREATE TABLE film_text (film_id INT PRIMARY KEY, title VARCHAR(50));\n";

    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser(), new MariaDb12DatabaseSchemaProvider()),
            TestContext.Current.CancellationToken);

    private static async Task<Element> BuildTriggerAsync(string triggerSql)
    {
        var model = await BuildModelAsync(Tables + triggerSql);

        return Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlTrigger);
    }

    [Fact]
    public async Task Trigger_IsModeledWithTimingEventAndBody()
    {
        var trigger = await BuildTriggerAsync(
            """
            CREATE TRIGGER ins_film AFTER INSERT ON film
            FOR EACH ROW
            BEGIN
              INSERT INTO film_text (film_id, title) VALUES (NEW.film_id, NEW.title);
            END;
            """);

        // The element name folds in the table so triggers of the same name on different tables
        // stay distinct; the bare name lives in RoutineName.
        Assert.Equal("film.ins_film", trigger.Name);
        Assert.Equal("ins_film", trigger.GetRequiredProperty<string>(MariaDbPropertyNames.RoutineName));
        Assert.Equal("AFTER", trigger.GetRequiredProperty<string>(MariaDbPropertyNames.Timing));
        Assert.Equal("INSERT", trigger.GetRequiredProperty<string>(MariaDbPropertyNames.Event));
        Assert.Equal(
            """
            BEGIN
              INSERT INTO film_text (film_id, title) VALUES (NEW.film_id, NEW.title);
            END
            """,
            trigger.GetRequiredProperty<string>(MariaDbPropertyNames.Body));
    }

    [Fact]
    public async Task Trigger_ReferencesItsTable()
    {
        var trigger = await BuildTriggerAsync(
            "CREATE TRIGGER t BEFORE UPDATE ON film FOR EACH ROW SET NEW.title = NEW.title;");

        var reference = trigger.GetRelationship(MariaDbRelationshipNames.TriggerTable)
            ?.Entries.OfType<Reference>().Single();

        Assert.NotNull(reference);
        Assert.Equal("film", reference.Name);
    }

    [Fact]
    public async Task Trigger_TableQualifierIsDropped()
    {
        // A trigger is not schema-scoped within a database, so a db qualifier is dropped from
        // both the trigger's name and its table, exactly as for a table.
        var trigger = await BuildTriggerAsync(
            "CREATE TRIGGER shop.t AFTER INSERT ON shop.film FOR EACH ROW SET @x = 1;");

        Assert.Equal("film.t", trigger.Name);
        Assert.Equal("t", trigger.GetRequiredProperty<string>(MariaDbPropertyNames.RoutineName));

        var reference = trigger.GetRelationship(MariaDbRelationshipNames.TriggerTable)
            ?.Entries.OfType<Reference>().Single();
        Assert.Equal("film", reference!.Name);
    }

    [Fact]
    public async Task Trigger_SortsAfterTablesAndRoutines()
    {
        // The DB extractor emits triggers last (tables, views, routines, then triggers). The
        // Merkle hash is order-sensitive, so the parser-based builder must adopt that order.
        var model = await BuildModelAsync(
            Tables
            + "CREATE FUNCTION f() RETURNS INT DETERMINISTIC RETURN 1;\n"
            + "CREATE TRIGGER t AFTER INSERT ON film FOR EACH ROW SET @x = 1;");

        var types = model.Elements.Select(i => i.Type).ToList();

        var triggerIndex = types.IndexOf(MariaDbElementTypes.SqlTrigger);
        var functionIndex = types.IndexOf(MariaDbElementTypes.SqlFunction);
        var lastTableIndex = types.LastIndexOf(MariaDbElementTypes.SqlTable);

        Assert.True(triggerIndex > functionIndex, "Trigger should sort after functions.");
        Assert.True(triggerIndex > lastTableIndex, "Trigger should sort after tables.");
    }

    [Fact]
    public async Task Triggers_AreOrderedByBareName()
    {
        // Triggers are ordered by their own name, not the folded table.trigger element name, to
        // match the DB extractor's ORDER BY TRIGGER_NAME.
        var model = await BuildModelAsync(
            Tables
            + "CREATE TRIGGER b_trig AFTER INSERT ON film_text FOR EACH ROW SET @x = 1;\n"
            + "CREATE TRIGGER a_trig AFTER INSERT ON film FOR EACH ROW SET @x = 1;");

        var triggerNames = model.Elements
            .Where(i => i.Type == MariaDbElementTypes.SqlTrigger)
            .Select(i => i.GetRequiredProperty<string>(MariaDbPropertyNames.RoutineName))
            .ToList();

        Assert.Equal(new[] { "a_trig", "b_trig" }, triggerNames);
    }

    [Fact]
    public async Task Trigger_OnUndeclaredTable_IsABuildError()
    {
        // The table a trigger fires on must be declared in the project, like any other
        // unresolved reference.
        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync(
            "CREATE TRIGGER t AFTER INSERT ON missing FOR EACH ROW SET @x = 1;"));

        Assert.Equal(SqlSourceException.UnresolvedReference, ex.Code);
        Assert.Contains("missing", ex.Message);
    }

    [Fact]
    public async Task DuplicateTriggerName_IsABuildError()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync(
            Tables
            + "CREATE TRIGGER t AFTER INSERT ON film FOR EACH ROW SET @x = 1;\n"
            + "CREATE TRIGGER t AFTER DELETE ON film_text FOR EACH ROW SET @x = 1;"));

        Assert.Equal(SqlSourceException.DuplicateDefinition, ex.Code);
        Assert.Contains("'t'", ex.Message);
    }
}

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

    // ---- Firing order and DEFINER (issue #215) ----

    private static async Task<IReadOnlyList<Element>> BuildTriggersAsync(string triggerSql)
    {
        var model = await BuildModelAsync(Tables + triggerSql);

        return model.Elements.Where(i => i.Type == MariaDbElementTypes.SqlTrigger).ToList();
    }

    private static int? ActionOrderOf(Element trigger)
        => trigger.GetProperty<int?>(MariaDbPropertyNames.ActionOrder);

    [Fact]
    public async Task Trigger_FollowsIsModeledAsAPosition()
    {
        // FOLLOWS names a trigger, but both engines report only the resulting position, so the
        // position is what the model carries.
        var triggers = await BuildTriggersAsync(
            "CREATE TRIGGER a BEFORE INSERT ON film FOR EACH ROW SET @x = 1;\n"
            + "CREATE TRIGGER b BEFORE INSERT ON film FOR EACH ROW FOLLOWS a SET @x = 2;");

        Assert.Equal(1, ActionOrderOf(Assert.Single(triggers, i => (string)i.Name! == "film.a")));
        Assert.Equal(2, ActionOrderOf(Assert.Single(triggers, i => (string)i.Name! == "film.b")));
    }

    [Fact]
    public async Task Trigger_PrecedesIsModeledAsAPosition()
    {
        var triggers = await BuildTriggersAsync(
            "CREATE TRIGGER a BEFORE INSERT ON film FOR EACH ROW SET @x = 1;\n"
            + "CREATE TRIGGER b BEFORE INSERT ON film FOR EACH ROW PRECEDES a SET @x = 2;");

        // b precedes a, so b is first even though a was declared first.
        Assert.Equal(1, ActionOrderOf(Assert.Single(triggers, i => (string)i.Name! == "film.b")));
        Assert.Equal(2, ActionOrderOf(Assert.Single(triggers, i => (string)i.Name! == "film.a")));
    }

    [Fact]
    public async Task Trigger_WithoutOrderingClause_FollowsDeclarationOrder()
    {
        // With no clause, both engines order by creation, which for a build is declaration
        // order.
        var triggers = await BuildTriggersAsync(
            "CREATE TRIGGER a BEFORE INSERT ON film FOR EACH ROW SET @x = 1;\n"
            + "CREATE TRIGGER b BEFORE INSERT ON film FOR EACH ROW SET @x = 2;");

        Assert.Equal(1, ActionOrderOf(Assert.Single(triggers, i => (string)i.Name! == "film.a")));
        Assert.Equal(2, ActionOrderOf(Assert.Single(triggers, i => (string)i.Name! == "film.b")));
    }

    [Fact]
    public async Task LoneTrigger_HasNoActionOrderProperty()
    {
        // Omit-when-default: a lone trigger in its group is always position 1, so recording it
        // would make the parsed model differ from an extracted one for no gain.
        var trigger = await BuildTriggerAsync(
            "CREATE TRIGGER a BEFORE INSERT ON film FOR EACH ROW SET @x = 1;");

        Assert.Null(ActionOrderOf(trigger));
    }

    [Fact]
    public async Task Triggers_AreGroupedByTimingAndEvent()
    {
        // ACTION_ORDER restarts per (table, timing, event) on both engines, so triggers that
        // do not share all three never compete for a position.
        var triggers = await BuildTriggersAsync(
            "CREATE TRIGGER a BEFORE INSERT ON film FOR EACH ROW SET @x = 1;\n"
            + "CREATE TRIGGER b AFTER INSERT ON film FOR EACH ROW SET @x = 2;\n"
            + "CREATE TRIGGER c BEFORE UPDATE ON film FOR EACH ROW SET @x = 3;\n"
            + "CREATE TRIGGER d BEFORE INSERT ON film_text FOR EACH ROW SET @x = 4;");

        // Each is alone in its group, so none records a position.
        Assert.All(triggers, i => Assert.Null(ActionOrderOf(i)));
    }

    [Fact]
    public async Task Trigger_FollowsUndeclaredTrigger_IsABuildError()
    {
        // FOLLOWS names a trigger that must exist, exactly like any other unresolved
        // reference; the engines reject it too.
        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync(
            Tables
            + "CREATE TRIGGER b BEFORE INSERT ON film FOR EACH ROW FOLLOWS nope SET @x = 1;"));

        Assert.Equal(SqlSourceException.UnresolvedReference, ex.Code);
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task Trigger_FollowsTriggerInAnotherGroup_IsABuildError()
    {
        // A trigger can only be ordered against one in the same table/timing/event group;
        // both engines reject anything else.
        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync(
            Tables
            + "CREATE TRIGGER a AFTER INSERT ON film FOR EACH ROW SET @x = 1;\n"
            + "CREATE TRIGGER b BEFORE INSERT ON film FOR EACH ROW FOLLOWS a SET @x = 2;"));

        Assert.Equal(SqlSourceException.UnresolvedReference, ex.Code);
    }

    [Fact]
    public async Task Trigger_DefinerIsModeled()
    {
        var trigger = await BuildTriggerAsync(
            "CREATE DEFINER = 'alice'@'%' TRIGGER a BEFORE INSERT ON film "
            + "FOR EACH ROW SET @x = 1;");

        Assert.Equal("alice@%", trigger.GetRequiredProperty<string>(MariaDbPropertyNames.Definer));
    }

    [Fact]
    public async Task Trigger_DefinerCurrentUser_IsNotModeled()
    {
        // Measured on both engines: DEFINER = CURRENT_USER and no DEFINER at all are
        // indistinguishable in the catalog, so neither is recorded.
        var trigger = await BuildTriggerAsync(
            "CREATE DEFINER = CURRENT_USER TRIGGER a BEFORE INSERT ON film "
            + "FOR EACH ROW SET @x = 1;");

        Assert.Null(trigger.GetProperty<string>(MariaDbPropertyNames.Definer));
    }

    [Fact]
    public async Task Trigger_WithoutDefiner_IsNotModeled()
    {
        var trigger = await BuildTriggerAsync(
            "CREATE TRIGGER a BEFORE INSERT ON film FOR EACH ROW SET @x = 1;");

        Assert.Null(trigger.GetProperty<string>(MariaDbPropertyNames.Definer));
    }

    [Fact]
    public async Task Triggers_AreOrderedSoAFollowsNeverForwardReferences()
    {
        // Deltas are scripted in model order and a trigger's FOLLOWS names the one before it,
        // so a trigger must appear after its predecessor. Both engines reject a FOLLOWS naming
        // a trigger that does not exist yet (measured), which is what a plain name ordering
        // would produce here: the desired firing order is the reverse of the name order.
        var model = await BuildModelAsync(Tables
            + "CREATE TRIGGER a_trig BEFORE INSERT ON film FOR EACH ROW SET @x = 1;\n"
            + "CREATE TRIGGER z_trig BEFORE INSERT ON film FOR EACH ROW PRECEDES a_trig SET @x = 2;");

        var names = model.Elements
            .Where(i => i.Type == MariaDbElementTypes.SqlTrigger)
            .Select(i => i.GetRequiredProperty<string>(MariaDbPropertyNames.RoutineName))
            .ToList();

        Assert.Equal(new[] { "z_trig", "a_trig" }, names);
    }

    [Fact]
    public async Task Trigger_RecordsThePredecessorItFollows()
    {
        var triggers = await BuildTriggersAsync(
            "CREATE TRIGGER a BEFORE INSERT ON film FOR EACH ROW SET @x = 1;\n"
            + "CREATE TRIGGER b BEFORE INSERT ON film FOR EACH ROW FOLLOWS a SET @x = 2;");

        var first = Assert.Single(triggers, i => (string)i.Name! == "film.a");
        var second = Assert.Single(triggers, i => (string)i.Name! == "film.b");

        // The first in a group follows nothing; the second names the one before it.
        Assert.Null(first.GetProperty<string>(MariaDbPropertyNames.FollowsTrigger));
        Assert.Equal("a", second.GetRequiredProperty<string>(MariaDbPropertyNames.FollowsTrigger));
    }

    [Fact]
    public async Task Trigger_PredecessorDoesNotParticipateInIdentity()
    {
        // FollowsTrigger is carried for scripting only: ActionOrder already states the
        // position, so a trigger must not re-diff because a sibling was renamed.
        var triggers = await BuildTriggersAsync(
            "CREATE TRIGGER a BEFORE INSERT ON film FOR EACH ROW SET @x = 1;\n"
            + "CREATE TRIGGER b BEFORE INSERT ON film FOR EACH ROW FOLLOWS a SET @x = 2;");

        var second = Assert.Single(triggers, i => (string)i.Name! == "film.b");

        var withoutPredecessor = MariaDbModelFactory.CreateTrigger(
            SqlName.Object("film"), "b", "BEFORE", "INSERT", "SET @x = 2", actionOrder: 2);

        Assert.True(HashUtility.HashesEqual(second.Hash, withoutPredecessor.Hash),
            "The trigger a trigger follows must not take part in its identity.");
    }

    [Fact]
    public async Task Trigger_MayFollowATriggerDeclaredLater()
    {
        // Declaration order must not matter in a declarative project (the invariant the whole
        // builder is validated against), so a FOLLOWS may name a trigger declared further down.
        // The engines require the target to exist at CREATE time, but that is a property of the
        // generated script's ordering, not of the source.
        var triggers = await BuildTriggersAsync(
            "CREATE TRIGGER b BEFORE INSERT ON film FOR EACH ROW FOLLOWS a SET @x = 2;\n"
            + "CREATE TRIGGER a BEFORE INSERT ON film FOR EACH ROW SET @x = 1;");

        Assert.Equal(1, ActionOrderOf(Assert.Single(triggers, i => (string)i.Name! == "film.a")));
        Assert.Equal(2, ActionOrderOf(Assert.Single(triggers, i => (string)i.Name! == "film.b")));
    }

    [Fact]
    public async Task TriggerOrdering_DoesNotDependOnDeclarationOrder()
    {
        // The same three triggers declared in two different orders must produce the same
        // firing order, and so the same model.
        var declaredForwards = await BuildTriggersAsync(
            "CREATE TRIGGER a BEFORE INSERT ON film FOR EACH ROW SET @x = 1;\n"
            + "CREATE TRIGGER b BEFORE INSERT ON film FOR EACH ROW FOLLOWS a SET @x = 2;\n"
            + "CREATE TRIGGER c BEFORE INSERT ON film FOR EACH ROW FOLLOWS b SET @x = 3;");

        var declaredBackwards = await BuildTriggersAsync(
            "CREATE TRIGGER c BEFORE INSERT ON film FOR EACH ROW FOLLOWS b SET @x = 3;\n"
            + "CREATE TRIGGER b BEFORE INSERT ON film FOR EACH ROW FOLLOWS a SET @x = 2;\n"
            + "CREATE TRIGGER a BEFORE INSERT ON film FOR EACH ROW SET @x = 1;");

        static IEnumerable<(string, int?)> Positions(IReadOnlyList<Element> triggers)
            => triggers
                .Select(i => ((string)i.Name!, ActionOrderOf(i)))
                .OrderBy(i => i.Item1, StringComparer.Ordinal);

        Assert.Equal(
            new[] { ("film.a", (int?)1), ("film.b", 2), ("film.c", 3) },
            Positions(declaredForwards));
        Assert.Equal(Positions(declaredForwards), Positions(declaredBackwards));
    }

    [Fact]
    public async Task Trigger_DefinerCurrentRole_IsNotModeled()
    {
        // Same as CURRENT_USER: resolved to an account at create time, so it means "whoever
        // deploys" and records nothing.
        var trigger = await BuildTriggerAsync(
            "CREATE DEFINER = CURRENT_ROLE TRIGGER a BEFORE INSERT ON film "
            + "FOR EACH ROW SET @x = 1;");

        Assert.Null(trigger.GetProperty<string>(MariaDbPropertyNames.Definer));
    }

    [Fact]
    public async Task Trigger_OrderedAgainstItself_IsABuildError()
    {
        // A cycle can never be placed. Reported rather than looping forever.
        var ex = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync(
            Tables
            + "CREATE TRIGGER a BEFORE INSERT ON film FOR EACH ROW FOLLOWS a SET @x = 1;"));

        Assert.Equal(SqlSourceException.UnresolvedReference, ex.Code);
        Assert.Contains("cycle", ex.Message);
    }

    [Fact]
    public async Task Triggers_InAFollowsCycle_AreABuildError()
    {
        // Both triggers are unplaceable, and the builder reports every source error in one
        // build rather than stopping at the first, so the two arrive aggregated.
        var ex = await Assert.ThrowsAsync<AggregateException>(() => BuildModelAsync(
            Tables
            + "CREATE TRIGGER a BEFORE INSERT ON film FOR EACH ROW FOLLOWS b SET @x = 1;\n"
            + "CREATE TRIGGER b BEFORE INSERT ON film FOR EACH ROW FOLLOWS a SET @x = 2;"));

        var errors = ex.InnerExceptions.OfType<SqlSourceException>().ToList();

        Assert.Equal(2, errors.Count);
        Assert.All(errors, i => Assert.Equal(SqlSourceException.UnresolvedReference, i.Code));

        // A cycle is named as one, rather than claiming the target is on another table.
        Assert.All(errors, i => Assert.Contains("cycle", i.Message));
    }
}

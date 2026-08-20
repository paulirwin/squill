using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Model-builder and script-generator tests for <c>CREATE TRIGGER</c> (issue #83).
/// </summary>
public class CreateTriggerModelTests
{
    // A table and a trigger function the trigger references, so the model is self-consistent.
    private const string PreambleSql =
        "CREATE TABLE film (\n"
        + "    film_id integer PRIMARY KEY,\n"
        + "    last_update timestamp NOT NULL\n"
        + ");\n"
        + "CREATE FUNCTION last_updated() RETURNS trigger LANGUAGE plpgsql\n"
        + "    AS $$ BEGIN NEW.last_update = now(); RETURN NEW; END $$;\n";

    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            TestContext.Current.CancellationToken);

    private static async Task<string> ScriptAgainstEmptyAsync(string sql)
    {
        var model = await BuildModelAsync(sql);
        var provider = new PostgresDatabaseProvider("Host=unused");

        return new PostgresScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, model, new Model()));
    }

    [Fact]
    public async Task Trigger_IsModeledWithItsFacets()
    {
        var model = await BuildModelAsync(PreambleSql + """
            CREATE TRIGGER last_updated
                BEFORE UPDATE ON film
                FOR EACH ROW
                EXECUTE FUNCTION last_updated();
            """);

        var trigger = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlTrigger);

        Assert.Equal("public.film.last_updated", trigger.Name);
        Assert.Equal("public", PostgresModelFactory.GetSchema(trigger));
        Assert.Equal("last_updated", trigger.GetProperty<string>(PostgresPropertyNames.RoutineName));
        Assert.Equal("BEFORE", trigger.GetProperty<string>(PostgresPropertyNames.Timing));
        Assert.Equal("UPDATE", trigger.GetProperty<string>(PostgresPropertyNames.Events));
        Assert.Equal("ROW", trigger.GetProperty<string>(PostgresPropertyNames.Level));
        // A bare function name is stored bare (not force-qualified with the table's schema),
        // so a built-in or search-path function resolves correctly at deploy time.
        Assert.Equal("last_updated", trigger.GetProperty<string>(PostgresPropertyNames.TriggerFunction));
        Assert.Equal("", trigger.GetProperty<string>(PostgresPropertyNames.FunctionArguments));
    }

    [Fact]
    public async Task Trigger_OrredEventsAreRenderedCanonically()
    {
        var model = await BuildModelAsync(PreambleSql + """
            CREATE TRIGGER film_fulltext_trigger
                BEFORE UPDATE OR INSERT ON film
                FOR EACH ROW
                EXECUTE FUNCTION tsvector_update_trigger('fulltext', 'pg_catalog.english', 'title');
            """);

        var trigger = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlTrigger);

        // Events are rendered in a fixed order (INSERT before UPDATE) regardless of how they
        // were written, so the model is canonical and hash-matches an extracted one.
        Assert.Equal("INSERT OR UPDATE", trigger.GetProperty<string>(PostgresPropertyNames.Events));
        Assert.Equal("tsvector_update_trigger", trigger.GetProperty<string>(PostgresPropertyNames.TriggerFunction));
        Assert.Equal("fulltext, pg_catalog.english, title",
            trigger.GetProperty<string>(PostgresPropertyNames.FunctionArguments));
    }

    [Fact]
    public async Task Trigger_UnresolvedTableIsAnError()
    {
        // No CREATE TABLE for the referenced table, so the trigger is an unresolved reference.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => BuildModelAsync(
            "CREATE FUNCTION f() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RETURN NEW; END $$;\n"
            + "CREATE TRIGGER t BEFORE UPDATE ON missing_table FOR EACH ROW EXECUTE FUNCTION f();"));

        Assert.Contains("missing_table", ex.ToString());
    }

    [Fact]
    public async Task Script_EmitsCreateTrigger()
    {
        var sql = await ScriptAgainstEmptyAsync(PreambleSql + """
            CREATE TRIGGER last_updated
                BEFORE UPDATE ON film
                FOR EACH ROW
                EXECUTE FUNCTION last_updated();
            """);

        Assert.Contains("CREATE TRIGGER \"last_updated\"", sql);
        Assert.Contains("BEFORE UPDATE ON \"film\"", sql);
        Assert.Contains("FOR EACH ROW", sql);
        Assert.Contains("EXECUTE FUNCTION \"last_updated\"();", sql);
    }

    [Fact]
    public async Task Script_EmitsTriggerFunctionArgumentsAsStringLiterals()
    {
        var sql = await ScriptAgainstEmptyAsync(PreambleSql + """
            CREATE TRIGGER film_fulltext_trigger
                BEFORE INSERT OR UPDATE ON film
                FOR EACH ROW
                EXECUTE FUNCTION tsvector_update_trigger('fulltext', 'pg_catalog.english', 'title');
            """);

        Assert.Contains(
            "EXECUTE FUNCTION \"tsvector_update_trigger\"('fulltext', 'pg_catalog.english', 'title');",
            sql);
    }

    [Fact]
    public async Task Script_TriggerIsCreatedAfterItsTableAndFunction()
    {
        var sql = await ScriptAgainstEmptyAsync(PreambleSql + """
            CREATE TRIGGER last_updated
                BEFORE UPDATE ON film
                FOR EACH ROW
                EXECUTE FUNCTION last_updated();
            """);

        var tableIndex = sql.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        var fnIndex = sql.IndexOf("CREATE OR REPLACE FUNCTION", StringComparison.Ordinal);
        var trigIndex = sql.IndexOf("CREATE TRIGGER", StringComparison.Ordinal);

        Assert.True(tableIndex >= 0 && fnIndex >= 0 && trigIndex >= 0);
        Assert.True(tableIndex < trigIndex, "trigger should be created after its table");
        Assert.True(fnIndex < trigIndex, "trigger should be created after its function");
    }

    [Fact]
    public async Task Script_DropTriggerNamesTheTable()
    {
        var model = await BuildModelAsync(PreambleSql + """
            CREATE TRIGGER last_updated
                BEFORE UPDATE ON film
                FOR EACH ROW
                EXECUTE FUNCTION last_updated();
            """);
        var provider = new PostgresDatabaseProvider("Host=unused");

        var options = new DeployOptions { DropObjectsNotInSource = true, BlockOnPossibleDataLoss = false };
        var sql = new PostgresScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, new Model(), model, options));

        Assert.Contains("DROP TRIGGER IF EXISTS \"last_updated\" ON \"film\";", sql);
    }

    // Issue #214: the optional declaration clauses. Each is stored only when declared, so an
    // ordinary trigger carries none of the properties below and does not re-diff.

    [Fact]
    public async Task WhenCondition_IsStoredRawAndCanonical()
    {
        var model = await BuildModelAsync(PreambleSql + """
            CREATE TRIGGER last_updated
                BEFORE UPDATE ON film
                FOR EACH ROW
                WHEN (OLD.last_update IS DISTINCT FROM NEW.last_update)
                EXECUTE FUNCTION last_updated();
            """);

        var trigger = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlTrigger);

        // The raw predicate is kept for scripting but excluded from identity: PostgreSQL
        // rewrites what it is given, so comparing raw text would see a change that is not one.
        var raw = Assert.Single(
            trigger.Properties,
            i => i.Name == PostgresPropertyNames.WhenCondition);
        Assert.False(raw.ParticipatesInIdentity);

        var normalized = Assert.Single(
            trigger.Properties,
            i => i.Name == PostgresPropertyNames.NormalizedWhenCondition);
        Assert.True(normalized.ParticipatesInIdentity);
        Assert.Equal(
            "(old.last_update IS DISTINCT FROM new.last_update)",
            normalized.Value);
    }

    /// <summary>
    /// Two spellings of one predicate produce one model. Measured: PostgreSQL stores
    /// <c>NEW.a &gt; 5</c> and <c>new.a&gt;5</c> alike, so if these hashed differently the
    /// trigger would be dropped and recreated on every deploy.
    /// </summary>
    [Fact]
    public async Task WhenCondition_SpellingVariations_HashTheSame()
    {
        const string Trigger =
            "CREATE TRIGGER last_updated BEFORE UPDATE ON film FOR EACH ROW\n"
            + "    WHEN ({0}) EXECUTE FUNCTION last_updated();";

        var declared = await BuildModelAsync(
            PreambleSql + string.Format(Trigger, "NEW.film_id > 5"));
        var respelled = await BuildModelAsync(
            PreambleSql + string.Format(Trigger, "new.film_id>5"));

        Assert.True(HashUtility.HashesEqual(declared.Hash, respelled.Hash));
    }

    /// <summary>A changed predicate IS a change, so it must not hash the same.</summary>
    [Fact]
    public async Task WhenCondition_ThatChanges_ChangesTheHash()
    {
        const string Trigger =
            "CREATE TRIGGER last_updated BEFORE UPDATE ON film FOR EACH ROW\n"
            + "    WHEN ({0}) EXECUTE FUNCTION last_updated();";

        var before = await BuildModelAsync(
            PreambleSql + string.Format(Trigger, "NEW.film_id > 5"));
        var after = await BuildModelAsync(
            PreambleSql + string.Format(Trigger, "NEW.film_id > 6"));

        Assert.False(HashUtility.HashesEqual(before.Hash, after.Hash));
    }

    [Fact]
    public async Task UpdateOfColumns_AreStoredInDeclaredOrder()
    {
        var model = await BuildModelAsync(PreambleSql + """
            CREATE TRIGGER last_updated
                AFTER UPDATE OF last_update, film_id ON film
                FOR EACH ROW
                EXECUTE FUNCTION last_updated();
            """);

        var trigger = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlTrigger);

        Assert.Equal("UPDATE", trigger.GetProperty<string>(PostgresPropertyNames.Events));
        Assert.Equal(
            "last_update, film_id",
            trigger.GetProperty<string>(PostgresPropertyNames.UpdateOfColumns));
    }

    [Fact]
    public async Task TransitionTables_AreStored()
    {
        var model = await BuildModelAsync(PreambleSql + """
            CREATE TRIGGER last_updated
                AFTER UPDATE ON film
                REFERENCING OLD TABLE AS before_rows NEW TABLE AS after_rows
                FOR EACH STATEMENT
                EXECUTE FUNCTION last_updated();
            """);

        var trigger = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlTrigger);

        Assert.Equal("before_rows",
            trigger.GetProperty<string>(PostgresPropertyNames.OldTransitionTable));
        Assert.Equal("after_rows",
            trigger.GetProperty<string>(PostgresPropertyNames.NewTransitionTable));
    }

    [Fact]
    public async Task ConstraintTrigger_StoresItsDeferrability()
    {
        var model = await BuildModelAsync(PreambleSql + """
            CREATE CONSTRAINT TRIGGER last_updated
                AFTER UPDATE ON film
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION last_updated();
            """);

        var trigger = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlTrigger);

        Assert.True(trigger.GetProperty<bool?>(PostgresPropertyNames.IsConstraintTrigger) == true);
        Assert.True(trigger.GetProperty<bool?>(PostgresPropertyNames.IsDeferrable) == true);
        Assert.True(trigger.GetProperty<bool?>(PostgresPropertyNames.IsInitiallyDeferred) == true);
    }

    /// <summary>
    /// An ordinary trigger stores none of the modifier properties. This is what keeps a model
    /// built before issue #214 hash-identical to one built after it.
    /// </summary>
    [Fact]
    public async Task PlainTrigger_StoresNoModifierProperties()
    {
        var model = await BuildModelAsync(PreambleSql + """
            CREATE TRIGGER last_updated BEFORE UPDATE ON film
                FOR EACH ROW EXECUTE FUNCTION last_updated();
            """);

        var trigger = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlTrigger);

        Assert.DoesNotContain(trigger.Properties, i =>
            i.Name is PostgresPropertyNames.WhenCondition
                or PostgresPropertyNames.NormalizedWhenCondition
                or PostgresPropertyNames.UpdateOfColumns
                or PostgresPropertyNames.OldTransitionTable
                or PostgresPropertyNames.NewTransitionTable
                or PostgresPropertyNames.IsConstraintTrigger
                or PostgresPropertyNames.IsDeferrable
                or PostgresPropertyNames.IsInitiallyDeferred);
    }

    [Fact]
    public async Task WhenCondition_IsScripted()
    {
        var script = await ScriptAgainstEmptyAsync(PreambleSql + """
            CREATE TRIGGER last_updated
                BEFORE UPDATE ON film
                FOR EACH ROW
                WHEN (OLD.last_update IS DISTINCT FROM NEW.last_update)
                EXECUTE FUNCTION last_updated();
            """);

        Assert.Contains("WHEN (", script);
        Assert.Contains("IS DISTINCT FROM", script);
    }

    /// <summary>
    /// UPDATE OF binds to the UPDATE event alone, so the column list is spliced into that one
    /// event rather than appended to the whole list.
    /// </summary>
    [Fact]
    public async Task UpdateOfColumns_AreScriptedOnTheUpdateEventOnly()
    {
        var script = await ScriptAgainstEmptyAsync(PreambleSql + """
            CREATE TRIGGER last_updated
                AFTER INSERT OR UPDATE OF last_update ON film
                FOR EACH ROW
                EXECUTE FUNCTION last_updated();
            """);

        Assert.Contains("INSERT OR UPDATE OF \"last_update\"", script);
    }

    [Fact]
    public async Task TransitionTables_AreScripted()
    {
        var script = await ScriptAgainstEmptyAsync(PreambleSql + """
            CREATE TRIGGER last_updated
                AFTER UPDATE ON film
                REFERENCING OLD TABLE AS before_rows NEW TABLE AS after_rows
                FOR EACH STATEMENT
                EXECUTE FUNCTION last_updated();
            """);

        Assert.Contains(
            "REFERENCING OLD TABLE AS \"before_rows\" NEW TABLE AS \"after_rows\"", script);
    }

    [Fact]
    public async Task ConstraintTrigger_IsScripted()
    {
        var script = await ScriptAgainstEmptyAsync(PreambleSql + """
            CREATE CONSTRAINT TRIGGER last_updated
                AFTER UPDATE ON film
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION last_updated();
            """);

        Assert.Contains("CREATE CONSTRAINT TRIGGER", script);
        Assert.Contains("DEFERRABLE INITIALLY DEFERRED", script);
    }
}

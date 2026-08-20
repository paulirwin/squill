using Squill.PostgresParser.Syntax;
using Squill.TestFramework;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// Parser tests for <c>CREATE TRIGGER</c> (issue #83). A trigger arrives via the
/// <c>createtrigstmt</c> grammar rule and captures its timing, events, level, target table
/// and the function it executes.
/// </summary>
public class CreateTriggerTests
{
    private static CreateTriggerStatement ParseOne(string text)
        => ParseAssertions.Single<CreateTriggerStatement>(new AntlrPostgresParser().Parse(text).Statements);

    [Fact]
    public void SimpleBeforeUpdateRowTrigger()
    {
        var stmt = ParseOne("""
            CREATE TRIGGER last_updated
                BEFORE UPDATE ON film
                FOR EACH ROW
                EXECUTE FUNCTION last_updated();
            """);

        Assert.Equal("last_updated", stmt.Name);
        Assert.Equal("film", stmt.Table.Segments[^1].Name);
        Assert.Equal(TriggerTiming.Before, stmt.Timing);
        Assert.Equal(TriggerEvents.Update, stmt.Events);
        Assert.Equal(TriggerLevel.Row, stmt.Level);
        Assert.Equal("last_updated", stmt.FunctionName!.Segments[^1].Name);
        Assert.Empty(stmt.FunctionArguments);
    }

    [Fact]
    public void OrredEventsAndFunctionArguments()
    {
        var stmt = ParseOne("""
            CREATE TRIGGER film_fulltext_trigger
                BEFORE INSERT OR UPDATE ON film
                FOR EACH ROW
                EXECUTE FUNCTION tsvector_update_trigger('fulltext', 'pg_catalog.english', 'title', 'description');
            """);

        Assert.Equal(TriggerEvents.Insert | TriggerEvents.Update, stmt.Events);
        Assert.Equal("tsvector_update_trigger", stmt.FunctionName!.Segments[^1].Name);
        Assert.Equal(
            new[] { "fulltext", "pg_catalog.english", "title", "description" },
            stmt.FunctionArguments);
    }

    [Fact]
    public void AfterDeleteStatementTrigger()
    {
        var stmt = ParseOne("""
            CREATE TRIGGER audit
                AFTER DELETE ON film
                FOR EACH STATEMENT
                EXECUTE PROCEDURE audit_fn();
            """);

        Assert.Equal(TriggerTiming.After, stmt.Timing);
        Assert.Equal(TriggerEvents.Delete, stmt.Events);
        Assert.Equal(TriggerLevel.Statement, stmt.Level);
    }

    [Fact]
    public void InsteadOfTriggerOnView()
    {
        var stmt = ParseOne("""
            CREATE TRIGGER v_ins
                INSTEAD OF INSERT ON some_view
                FOR EACH ROW
                EXECUTE FUNCTION v_ins_fn();
            """);

        Assert.Equal(TriggerTiming.InsteadOf, stmt.Timing);
        Assert.Equal(TriggerEvents.Insert, stmt.Events);
    }

    [Fact]
    public void SchemaQualifiedTableAndFunction()
    {
        var stmt = ParseOne("""
            CREATE TRIGGER t
                BEFORE INSERT ON app.film
                FOR EACH ROW
                EXECUTE FUNCTION app.set_defaults();
            """);

        Assert.Equal("app", stmt.Table.Segments[0].Name);
        Assert.Equal("film", stmt.Table.Segments[^1].Name);
        Assert.Equal("app", stmt.FunctionName!.Segments[0].Name);
        Assert.Equal("set_defaults", stmt.FunctionName.Segments[^1].Name);
    }

    [Fact]
    public void SourcePositionIsRecorded()
    {
        var stmt = ParseOne("""


            CREATE TRIGGER last_updated
                BEFORE UPDATE ON film
                FOR EACH ROW
                EXECUTE FUNCTION last_updated();
            """);

        Assert.Equal(3, stmt.Line);
        Assert.Equal(1, stmt.Column);
    }

    // Issue #214: the four declaration forms below used to throw NotImplementedException even
    // though the grammar accepts all of them. Each changes how often, or whether, the trigger
    // body runs, so dropping one silently would deploy a trigger that behaves differently from
    // the one that was declared.

    [Fact]
    public void WhenCondition_IsCaptured()
    {
        var stmt = ParseOne("""
            CREATE TRIGGER audit
                BEFORE UPDATE ON film
                FOR EACH ROW
                WHEN (OLD.title IS DISTINCT FROM NEW.title)
                EXECUTE FUNCTION audit_fn();
            """);

        Assert.NotNull(stmt.WhenCondition);
        Assert.Equal(
            "(old.title IS DISTINCT FROM new.title)",
            ExpressionNormalizer.TryNormalize(stmt.WhenCondition!, out var canonical)
                ? canonical
                : null);
    }

    [Fact]
    public void WhenCondition_IsAbsentWhenNotDeclared()
    {
        var stmt = ParseOne("""
            CREATE TRIGGER audit BEFORE UPDATE ON film
                FOR EACH ROW EXECUTE FUNCTION audit_fn();
            """);

        Assert.Null(stmt.WhenCondition);
    }

    [Fact]
    public void UpdateOfColumns_AreCaptured()
    {
        var stmt = ParseOne("""
            CREATE TRIGGER audit
                AFTER UPDATE OF title, rating ON film
                FOR EACH ROW
                EXECUTE FUNCTION audit_fn();
            """);

        Assert.Equal(TriggerEvents.Update, stmt.Events);
        Assert.Equal(new[] { "title", "rating" }, stmt.UpdateOfColumns.Select(i => i.Name));
    }

    /// <summary>
    /// The declared column order is kept rather than sorted. Measured: PostgreSQL stores
    /// <c>UPDATE OF b, a</c> as tgattr <c>3 2</c> and renders it back as <c>b, a</c>, so
    /// sorting would rewrite the user's DDL on the next script.
    /// </summary>
    [Fact]
    public void UpdateOfColumns_KeepDeclaredOrder()
    {
        var stmt = ParseOne("""
            CREATE TRIGGER audit AFTER UPDATE OF rating, title ON film
                FOR EACH ROW EXECUTE FUNCTION audit_fn();
            """);

        Assert.Equal(new[] { "rating", "title" }, stmt.UpdateOfColumns.Select(i => i.Name));
    }

    /// <summary>
    /// UPDATE OF applies only to the UPDATE event, and may sit alongside other events. The
    /// event set is unaffected by the column restriction.
    /// </summary>
    [Fact]
    public void UpdateOfColumns_AlongsideOtherEvents()
    {
        var stmt = ParseOne("""
            CREATE TRIGGER audit
                AFTER INSERT OR UPDATE OF title OR DELETE ON film
                FOR EACH ROW
                EXECUTE FUNCTION audit_fn();
            """);

        Assert.Equal(
            TriggerEvents.Insert | TriggerEvents.Update | TriggerEvents.Delete,
            stmt.Events);
        Assert.Equal(new[] { "title" }, stmt.UpdateOfColumns.Select(i => i.Name));
    }

    [Fact]
    public void ReferencingTransitionTables_AreCaptured()
    {
        var stmt = ParseOne("""
            CREATE TRIGGER audit
                AFTER UPDATE ON film
                REFERENCING OLD TABLE AS before_rows NEW TABLE AS after_rows
                FOR EACH STATEMENT
                EXECUTE FUNCTION audit_fn();
            """);

        Assert.Equal("before_rows", stmt.OldTransitionTable);
        Assert.Equal("after_rows", stmt.NewTransitionTable);
    }

    /// <summary>Either transition table may be declared on its own.</summary>
    [Theory]
    [InlineData("REFERENCING OLD TABLE AS only_old", "only_old", null)]
    [InlineData("REFERENCING NEW TABLE AS only_new", null, "only_new")]
    public void ReferencingTransitionTables_MayAppearIndependently(
        string referencing, string? expectedOld, string? expectedNew)
    {
        var stmt = ParseOne($"""
            CREATE TRIGGER audit AFTER UPDATE ON film
                {referencing}
                FOR EACH STATEMENT EXECUTE FUNCTION audit_fn();
            """);

        Assert.Equal(expectedOld, stmt.OldTransitionTable);
        Assert.Equal(expectedNew, stmt.NewTransitionTable);
    }

    /// <summary>The <c>AS</c> is optional in the grammar and carries no meaning.</summary>
    [Fact]
    public void ReferencingTransitionTables_WithoutAs()
    {
        var stmt = ParseOne("""
            CREATE TRIGGER audit AFTER UPDATE ON film
                REFERENCING NEW TABLE after_rows
                FOR EACH STATEMENT EXECUTE FUNCTION audit_fn();
            """);

        Assert.Equal("after_rows", stmt.NewTransitionTable);
    }

    [Fact]
    public void ConstraintTrigger_IsCaptured()
    {
        var stmt = ParseOne("""
            CREATE CONSTRAINT TRIGGER check_fk
                AFTER INSERT ON film
                DEFERRABLE INITIALLY DEFERRED
                FOR EACH ROW
                EXECUTE FUNCTION check_fk_fn();
            """);

        Assert.True(stmt.IsConstraintTrigger);
        Assert.True(stmt.IsDeferrable);
        Assert.True(stmt.IsInitiallyDeferred);
        Assert.Equal(TriggerTiming.After, stmt.Timing);
        Assert.Equal(TriggerLevel.Row, stmt.Level);
    }

    /// <summary>
    /// A constraint trigger without a deferrability clause is NOT DEFERRABLE INITIALLY
    /// IMMEDIATE, matching pg_trigger's tgdeferrable/tginitdeferred both reading false.
    /// </summary>
    [Fact]
    public void ConstraintTrigger_DefaultsToNotDeferrable()
    {
        var stmt = ParseOne("""
            CREATE CONSTRAINT TRIGGER check_fk AFTER INSERT ON film
                FOR EACH ROW EXECUTE FUNCTION check_fk_fn();
            """);

        Assert.True(stmt.IsConstraintTrigger);
        Assert.False(stmt.IsDeferrable);
        Assert.False(stmt.IsInitiallyDeferred);
    }

    [Fact]
    public void ConstraintTrigger_SupportsWhenCondition()
    {
        var stmt = ParseOne("""
            CREATE CONSTRAINT TRIGGER check_fk AFTER INSERT ON film
                FOR EACH ROW WHEN (NEW.rating IS NOT NULL)
                EXECUTE FUNCTION check_fk_fn();
            """);

        Assert.True(stmt.IsConstraintTrigger);
        Assert.NotNull(stmt.WhenCondition);
    }

    /// <summary>An ordinary trigger is not a constraint trigger and is never deferrable.</summary>
    [Fact]
    public void PlainTrigger_IsNotAConstraintTrigger()
    {
        var stmt = ParseOne("""
            CREATE TRIGGER audit BEFORE UPDATE ON film
                FOR EACH ROW EXECUTE FUNCTION audit_fn();
            """);

        Assert.False(stmt.IsConstraintTrigger);
        Assert.False(stmt.IsDeferrable);
        Assert.Empty(stmt.UpdateOfColumns);
        Assert.Null(stmt.OldTransitionTable);
        Assert.Null(stmt.NewTransitionTable);
    }
}

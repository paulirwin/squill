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
}

using Squill.MariaDbParser;
using Squill.MariaDbParser.Syntax;

namespace Squill.MariaDbParser.Tests;

/// <summary>
/// Parser-level tests for CREATE TRIGGER (issue #100), asserting the syntax tree the mapper
/// produces. Model-level concerns (element shape, script generation) are covered in
/// Squill.Provider.MariaDb.Tests. Mirrors <see cref="CreateFunctionTests"/>.
/// </summary>
public class CreateTriggerTests
{
    private static CreateTriggerStatement ParseOne(string text)
    {
        var root = new AntlrMariaDbParser().Parse(text);

        return Assert.IsType<CreateTriggerStatement>(Assert.Single(root.Statements));
    }

    [Fact]
    public void CreateTrigger_AfterInsert()
    {
        var statement = ParseOne(
            """
            CREATE TRIGGER ins_film AFTER INSERT ON film
            FOR EACH ROW
            BEGIN
              INSERT INTO film_text (film_id) VALUES (NEW.film_id);
            END;
            """);

        Assert.Equal("ins_film", statement.Name.Name);
        Assert.Equal("AFTER", statement.Timing);
        Assert.Equal("INSERT", statement.Event);
        Assert.Equal("film", statement.Table.Name);
        Assert.False(statement.OrReplace);
    }

    [Fact]
    public void CreateTrigger_BeforeUpdate()
    {
        var statement = ParseOne(
            "CREATE TRIGGER t BEFORE UPDATE ON film FOR EACH ROW SET NEW.title = NEW.title;");

        Assert.Equal("BEFORE", statement.Timing);
        Assert.Equal("UPDATE", statement.Event);
    }

    [Fact]
    public void CreateTrigger_AfterDelete()
    {
        var statement = ParseOne(
            "CREATE TRIGGER t AFTER DELETE ON film FOR EACH ROW "
            + "DELETE FROM film_text WHERE film_id = OLD.film_id;");

        Assert.Equal("AFTER", statement.Timing);
        Assert.Equal("DELETE", statement.Event);
    }

    [Fact]
    public void CreateTrigger_TimingAndEventAreUpperCased()
    {
        // Both engines report ACTION_TIMING / EVENT_MANIPULATION upper-cased, so the parser
        // normalizes lower-case source to match.
        var statement = ParseOne(
            "create trigger t after insert on film for each row set @x = 1;");

        Assert.Equal("AFTER", statement.Timing);
        Assert.Equal("INSERT", statement.Event);
    }

    [Fact]
    public void CreateTrigger_OrReplace()
    {
        // OR REPLACE is MariaDB-specific syntax; it affects how the trigger is created, not the
        // schema state, so it is recorded but does not reach the model.
        var statement = ParseOne(
            "CREATE OR REPLACE TRIGGER t AFTER INSERT ON film FOR EACH ROW SET @x = 1;");

        Assert.True(statement.OrReplace);
    }

    [Fact]
    public void CreateTrigger_QualifiedTriggerName()
    {
        var statement = ParseOne(
            "CREATE TRIGGER shop.t AFTER INSERT ON film FOR EACH ROW SET @x = 1;");

        Assert.Equal(2, statement.Name.Segments.Count);
        Assert.Equal("shop", statement.Name.Segments[0].Name);
        Assert.Equal("t", statement.Name.Name);
    }

    [Fact]
    public void CreateTrigger_BacktickQuotedNamesAreUnquoted()
    {
        var statement = ParseOne(
            "CREATE TRIGGER `my trig` AFTER INSERT ON `my table` FOR EACH ROW SET @x = 1;");

        Assert.Equal("my trig", statement.Name.Name);
        Assert.Equal("my table", statement.Table.Name);
    }

    [Fact]
    public void CreateTrigger_BeginEndBodyIsHeldVerbatim()
    {
        // The body is exactly the characters it spans in the source — the BEGIN ... END block —
        // because both engines return ACTION_STATEMENT that way.
        var statement = ParseOne(
            """
            CREATE TRIGGER ins_film AFTER INSERT ON film
            FOR EACH ROW
            BEGIN
              INSERT INTO film_text (film_id, title) VALUES (NEW.film_id, NEW.title);
            END;
            """);

        Assert.Equal(
            """
            BEGIN
              INSERT INTO film_text (film_id, title) VALUES (NEW.film_id, NEW.title);
            END
            """,
            statement.Body);
    }

    [Fact]
    public void CreateTrigger_SingleStatementBodyIsHeldVerbatim()
    {
        var statement = ParseOne(
            "CREATE TRIGGER t AFTER DELETE ON film FOR EACH ROW "
            + "DELETE FROM film_text WHERE film_id = OLD.film_id;");

        Assert.Equal("DELETE FROM film_text WHERE film_id = OLD.film_id", statement.Body);
    }
}

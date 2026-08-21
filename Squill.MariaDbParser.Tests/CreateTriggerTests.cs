using Squill.MariaDbParser;
using Squill.MariaDbParser.Syntax;
using Squill.TestFramework;

namespace Squill.MariaDbParser.Tests;

/// <summary>
/// Parser-level tests for CREATE TRIGGER (issue #100), asserting the syntax tree the mapper
/// produces. Model-level concerns (element shape, script generation) are covered in
/// Squill.Provider.MariaDb.Tests. Mirrors <see cref="CreateFunctionTests"/>.
/// </summary>
public class CreateTriggerTests
{
    private static CreateTriggerStatement ParseOne(string text)
        => ParseAssertions.Single<CreateTriggerStatement>(new AntlrMariaDbParser().Parse(text).Statements);

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

    // ---- FOLLOWS / PRECEDES ordering and DEFINER (issue #215) ----

    [Fact]
    public void CreateTrigger_FollowsIsRead()
    {
        var statement = ParseOne(
            "CREATE TRIGGER t2 BEFORE INSERT ON film FOR EACH ROW FOLLOWS t1 SET @x = 1;");

        Assert.Equal(TriggerOrderPlacement.Follows, statement.OrderPlacement);
        Assert.Equal("t1", statement.OtherTrigger?.Name);
    }

    [Fact]
    public void CreateTrigger_PrecedesIsRead()
    {
        var statement = ParseOne(
            "CREATE TRIGGER t0 BEFORE INSERT ON film FOR EACH ROW PRECEDES t1 SET @x = 1;");

        Assert.Equal(TriggerOrderPlacement.Precedes, statement.OrderPlacement);
        Assert.Equal("t1", statement.OtherTrigger?.Name);
    }

    [Fact]
    public void CreateTrigger_WithoutOrderingClauseHasNoPlacement()
    {
        var statement = ParseOne(
            "CREATE TRIGGER t BEFORE INSERT ON film FOR EACH ROW SET @x = 1;");

        Assert.Null(statement.OrderPlacement);
        Assert.Null(statement.OtherTrigger);
    }

    [Fact]
    public void CreateTrigger_DefinerIsRead()
    {
        var statement = ParseOne(
            "CREATE DEFINER = 'alice'@'%' TRIGGER t BEFORE INSERT ON film "
            + "FOR EACH ROW SET @x = 1;");

        Assert.Equal("alice", statement.Definer?.User);
        Assert.Equal("%", statement.Definer?.Host);
        Assert.False(statement.Definer?.IsCurrentUser);
    }

    [Fact]
    public void CreateTrigger_DefinerCurrentUserIsRead()
    {
        // CURRENT_USER is carried as a distinct flag rather than a name: both engines resolve
        // it to whoever ran the DDL, which is the same thing omitting DEFINER means.
        var statement = ParseOne(
            "CREATE DEFINER = CURRENT_USER TRIGGER t BEFORE INSERT ON film "
            + "FOR EACH ROW SET @x = 1;");

        Assert.True(statement.Definer?.IsCurrentUser);
        Assert.Null(statement.Definer?.User);
    }

    [Theory]
    [InlineData("'alice'@'%'")]
    [InlineData("`alice`@`%`")]
    [InlineData("alice@`%`")]
    public void CreateTrigger_DefinerQuotingIsStripped(string definer)
    {
        // Either half may be bare, backtick-quoted or string-quoted; the catalog reports both
        // unquoted, so every spelling reduces to the same account.
        var statement = ParseOne(
            $"CREATE DEFINER = {definer} TRIGGER t BEFORE INSERT ON film "
            + "FOR EACH ROW SET @x = 1;");

        Assert.Equal("alice", statement.Definer?.User);
        Assert.Equal("%", statement.Definer?.Host);
    }

    [Fact]
    public void CreateTrigger_DefinerWithoutHost()
    {
        var statement = ParseOne(
            "CREATE DEFINER = alice TRIGGER t BEFORE INSERT ON film FOR EACH ROW SET @x = 1;");

        Assert.Equal("alice", statement.Definer?.User);
        Assert.Null(statement.Definer?.Host);
        Assert.Equal("alice", statement.Definer?.Account);
    }

    [Fact]
    public void CreateTrigger_QuotedCurrentUserIsAnAccountName()
    {
        // CURRENT_USER is a keyword that can also be an identifier, so the bare keyword is
        // recognized by its token. A quoted 'CURRENT_USER' is an ordinary account name and must
        // not be folded into the keyword form.
        var statement = ParseOne(
            "CREATE DEFINER = 'CURRENT_USER'@'localhost' TRIGGER t BEFORE INSERT ON film "
            + "FOR EACH ROW SET @x = 1;");

        Assert.False(statement.Definer?.IsCurrentUser);
        Assert.Equal("CURRENT_USER", statement.Definer?.User);
        Assert.Equal("localhost", statement.Definer?.Host);
    }

    [Fact]
    public void CreateTrigger_DefinerCurrentRoleIsRead()
    {
        // CURRENT_ROLE is a MariaDB-only spelling and, like CURRENT_USER, is resolved to an
        // account when the trigger is created. It is also in keywordsCanBeId, so it arrives
        // through simpleUserName and has to be recognized by token or it would model as a
        // literal account named "CURRENT_ROLE".
        var statement = ParseOne(
            "CREATE DEFINER = CURRENT_ROLE TRIGGER t BEFORE INSERT ON film "
            + "FOR EACH ROW SET @x = 1;");

        Assert.True(statement.Definer?.IsCurrentUser);
        Assert.Null(statement.Definer?.User);
        Assert.Null(statement.Definer?.Account);
    }

    [Fact]
    public void CreateTrigger_WithoutDefinerHasNone()
    {
        var statement = ParseOne(
            "CREATE TRIGGER t BEFORE INSERT ON film FOR EACH ROW SET @x = 1;");

        Assert.Null(statement.Definer);
    }
}

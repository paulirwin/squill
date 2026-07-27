using Squill.MariaDbParser;
using Squill.MariaDbParser.Syntax;
using Squill.TestFramework;

namespace Squill.MariaDbParser.Tests;

/// <summary>
/// Parser-level tests for CREATE EVENT (issue #122), asserting the syntax tree the mapper
/// produces. Model-level concerns (element shape, script generation) are covered in
/// Squill.Provider.MariaDb.Tests. Mirrors <see cref="CreateTriggerTests"/>.
/// </summary>
public class CreateEventTests
{
    private static CreateEventStatement ParseOne(string text)
        => ParseAssertions.Single<CreateEventStatement>(new AntlrMariaDbParser().Parse(text).Statements);

    [Fact]
    public void CreateEvent_OneTime()
    {
        var statement = ParseOne(
            "CREATE EVENT purge_audit ON SCHEDULE AT '2030-01-01 00:00:00' "
            + "DO DELETE FROM audit_log WHERE created < NOW();");

        Assert.Equal("purge_audit", statement.Name.Name);
        Assert.Equal("ONE TIME", statement.EventType);
        Assert.Equal("2030-01-01 00:00:00", statement.ExecuteAt);
        Assert.Null(statement.IntervalValue);
        Assert.Null(statement.IntervalField);
        Assert.Equal("DELETE FROM audit_log WHERE created < NOW()", statement.Body);
    }

    [Fact]
    public void CreateEvent_Recurring()
    {
        var statement = ParseOne(
            "CREATE EVENT rollup ON SCHEDULE EVERY 1 DAY STARTS '2030-01-01 00:00:00' "
            + "DO INSERT INTO stats (n) VALUES (1);");

        Assert.Equal("RECURRING", statement.EventType);
        Assert.Equal("1", statement.IntervalValue);
        Assert.Equal("DAY", statement.IntervalField);
        Assert.Equal("2030-01-01 00:00:00", statement.Starts);
        Assert.Null(statement.Ends);
        Assert.Null(statement.ExecuteAt);
    }

    [Fact]
    public void CreateEvent_RecurringWithStartsAndEnds()
    {
        var statement = ParseOne(
            "CREATE EVENT rollup ON SCHEDULE EVERY 2 HOUR "
            + "STARTS '2030-01-01 00:00:00' ENDS '2031-01-01 00:00:00' "
            + "DO INSERT INTO stats (n) VALUES (1);");

        Assert.Equal("2", statement.IntervalValue);
        Assert.Equal("HOUR", statement.IntervalField);
        Assert.Equal("2030-01-01 00:00:00", statement.Starts);
        Assert.Equal("2031-01-01 00:00:00", statement.Ends);
    }

    [Fact]
    public void CreateEvent_IntervalFieldIsUpperCased()
    {
        // Both engines report INTERVAL_FIELD upper-cased in information_schema.EVENTS, so the
        // parser upper-cases it to match — otherwise a lower-cased declaration would never
        // hash-match an extracted model.
        var statement = ParseOne(
            "CREATE EVENT e ON SCHEDULE every 1 week starts '2030-01-01 00:00:00' DO SELECT 1;");

        Assert.Equal("WEEK", statement.IntervalField);
    }

    [Fact]
    public void CreateEvent_CompoundIntervalField()
    {
        // A compound interval is written EVERY '2:3' DAY_HOUR, and the catalog reports the
        // value space-separated ('2 3'). The parser normalizes to the catalog's form.
        var statement = ParseOne(
            "CREATE EVENT e ON SCHEDULE EVERY '2:3' DAY_HOUR STARTS '2030-01-01 00:00:00' "
            + "DO SELECT 1;");

        Assert.Equal("2 3", statement.IntervalValue);
        Assert.Equal("DAY_HOUR", statement.IntervalField);
    }

    [Fact]
    public void CreateEvent_OnCompletionPreserve()
    {
        var statement = ParseOne(
            "CREATE EVENT e ON SCHEDULE AT '2030-01-01 00:00:00' ON COMPLETION PRESERVE "
            + "DO SELECT 1;");

        Assert.True(statement.PreserveOnCompletion);
    }

    [Fact]
    public void CreateEvent_OnCompletionNotPreserveIsTheDefault()
    {
        var withoutClause = ParseOne(
            "CREATE EVENT e ON SCHEDULE AT '2030-01-01 00:00:00' DO SELECT 1;");
        var explicitClause = ParseOne(
            "CREATE EVENT e ON SCHEDULE AT '2030-01-01 00:00:00' ON COMPLETION NOT PRESERVE "
            + "DO SELECT 1;");

        Assert.False(withoutClause.PreserveOnCompletion);
        Assert.False(explicitClause.PreserveOnCompletion);
    }

    [Fact]
    public void CreateEvent_Disable()
    {
        var statement = ParseOne(
            "CREATE EVENT e ON SCHEDULE AT '2030-01-01 00:00:00' DISABLE DO SELECT 1;");

        Assert.Equal("DISABLED", statement.Status);
    }

    [Fact]
    public void CreateEvent_EnabledIsTheDefault()
    {
        var statement = ParseOne(
            "CREATE EVENT e ON SCHEDULE AT '2030-01-01 00:00:00' DO SELECT 1;");

        Assert.Equal("ENABLED", statement.Status);
    }

    [Fact]
    public void CreateEvent_DisableOnSlave()
    {
        // MariaDB reports this status as SLAVESIDE_DISABLED and MySQL as
        // REPLICA_SIDE_DISABLED. The parser records the MariaDB spelling; the extractor
        // normalizes MySQL's onto it so one declaration matches on both engines.
        var statement = ParseOne(
            "CREATE EVENT e ON SCHEDULE AT '2030-01-01 00:00:00' DISABLE ON SLAVE DO SELECT 1;");

        Assert.Equal("SLAVESIDE_DISABLED", statement.Status);
    }

    [Fact]
    public void CreateEvent_Comment()
    {
        var statement = ParseOne(
            "CREATE EVENT e ON SCHEDULE AT '2030-01-01 00:00:00' COMMENT 'nightly purge' "
            + "DO SELECT 1;");

        Assert.Equal("nightly purge", statement.Comment);
    }

    [Fact]
    public void CreateEvent_NoCommentIsNull()
    {
        var statement = ParseOne(
            "CREATE EVENT e ON SCHEDULE AT '2030-01-01 00:00:00' DO SELECT 1;");

        Assert.Null(statement.Comment);
    }

    [Fact]
    public void CreateEvent_BeginEndBodyIsVerbatim()
    {
        var statement = ParseOne(
            """
            CREATE EVENT e ON SCHEDULE AT '2030-01-01 00:00:00' DO
            BEGIN
              INSERT INTO stats (n) VALUES (1);
            END;
            """);

        Assert.StartsWith("BEGIN", statement.Body);
        Assert.EndsWith("END", statement.Body);
        Assert.Contains("INSERT INTO stats (n) VALUES (1);", statement.Body);
    }

    [Fact]
    public void CreateEvent_QualifiedName()
    {
        var statement = ParseOne(
            "CREATE EVENT mydb.rollup ON SCHEDULE AT '2030-01-01 00:00:00' DO SELECT 1;");

        Assert.Equal("rollup", statement.Name.Name);
    }

    [Fact]
    public void CreateEvent_IfNotExists()
    {
        var statement = ParseOne(
            "CREATE EVENT IF NOT EXISTS e ON SCHEDULE AT '2030-01-01 00:00:00' DO SELECT 1;");

        Assert.Equal("e", statement.Name.Name);
    }

    [Fact]
    public void CreateEvent_RecurringWithoutStartsRecordsNoStarts()
    {
        // The parser records the schedule as written; a missing STARTS is rejected by the
        // model builder, which can point the diagnostic at the source position. See
        // CreateEventModelTests.Event_RecurringWithoutStartsIsAnError.
        var statement = ParseOne("CREATE EVENT e ON SCHEDULE EVERY 1 DAY DO SELECT 1;");

        Assert.Equal("RECURRING", statement.EventType);
        Assert.Null(statement.Starts);
    }

    [Fact]
    public void CreateEvent_NonConstantExecuteAtIsRecordedVerbatim()
    {
        // AT CURRENT_TIMESTAMP + INTERVAL 1 DAY is resolved to an absolute timestamp when the
        // event is created, so it cannot round-trip — but that judgement belongs to the model
        // builder. The parser keeps the whole expression, offsets included, so the builder can
        // name it in the diagnostic.
        var statement = ParseOne(
            "CREATE EVENT e ON SCHEDULE AT CURRENT_TIMESTAMP + INTERVAL 1 DAY DO SELECT 1;");

        Assert.Equal("CURRENT_TIMESTAMP + INTERVAL 1 DAY", statement.ExecuteAt);
    }
}

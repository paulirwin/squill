using Squill.Core;
using Squill.TestFramework;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Tests that CREATE EVENT maps to the expected model element (issue #122). An event is a
/// standalone, clock-scheduled object — unlike a trigger it is not bound to a table — so its
/// element name is its own name, and the schedule facets recorded are exactly the ones
/// information_schema.EVENTS reports back, so a parsed model hash-matches an extracted one.
/// </summary>
public class CreateEventModelTests
{
    // A minimal table the event bodies write to, so the workspace validates.
    private const string Tables =
        "CREATE TABLE stats (n INT PRIMARY KEY);\n";

    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser(), new MariaDb12DatabaseSchemaProvider()),
            TestContext.Current.CancellationToken);

    private static async Task<Element> BuildEventAsync(string eventSql)
    {
        var model = await BuildModelAsync(Tables + eventSql);

        return Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlEvent);
    }

    [Fact]
    public async Task Event_OneTimeIsModeledWithExecuteAt()
    {
        var element = await BuildEventAsync(
            "CREATE EVENT rollup ON SCHEDULE AT '2030-01-01 00:00:00' "
            + "DO INSERT INTO stats (n) VALUES (1);");

        Assert.Equal("rollup", element.Name);
        Assert.Equal("ONE TIME", element.GetRequiredProperty<string>(MariaDbPropertyNames.EventType));
        Assert.Equal("2030-01-01 00:00:00", element.GetRequiredProperty<string>(MariaDbPropertyNames.ExecuteAt));
        Assert.Equal(
            "INSERT INTO stats (n) VALUES (1)",
            element.GetRequiredProperty<string>(MariaDbPropertyNames.Body));
    }

    [Fact]
    public async Task Event_RecurringIsModeledWithIntervalAndStarts()
    {
        var element = await BuildEventAsync(
            "CREATE EVENT rollup ON SCHEDULE EVERY 1 DAY STARTS '2030-01-01 00:00:00' "
            + "DO INSERT INTO stats (n) VALUES (1);");

        Assert.Equal("RECURRING", element.GetRequiredProperty<string>(MariaDbPropertyNames.EventType));
        Assert.Equal("1", element.GetRequiredProperty<string>(MariaDbPropertyNames.IntervalValue));
        Assert.Equal("DAY", element.GetRequiredProperty<string>(MariaDbPropertyNames.IntervalField));
        Assert.Equal("2030-01-01 00:00:00", element.GetRequiredProperty<string>(MariaDbPropertyNames.Starts));
    }

    [Fact]
    public async Task Event_EndsIsRecordedWhenWritten()
    {
        var element = await BuildEventAsync(
            "CREATE EVENT rollup ON SCHEDULE EVERY 2 HOUR STARTS '2030-01-01 00:00:00' "
            + "ENDS '2031-01-01 00:00:00' DO INSERT INTO stats (n) VALUES (1);");

        Assert.Equal("2031-01-01 00:00:00", element.GetRequiredProperty<string>(MariaDbPropertyNames.Ends));
    }

    [Fact]
    public async Task Event_OmitsDefaultedFacets()
    {
        // The catalog always reports every facet with defaults filled in, so a facet equal to
        // its default is never stored — that is what lets a parsed model hash-match an
        // extracted one. ENABLED status and NOT PRESERVE are the defaults.
        var element = await BuildEventAsync(
            "CREATE EVENT rollup ON SCHEDULE AT '2030-01-01 00:00:00' DO SELECT 1;");

        Assert.Null(element.GetProperty<string>(MariaDbPropertyNames.Status));
        Assert.DoesNotContain(
            element.Properties,
            i => i.Name == MariaDbPropertyNames.PreserveOnCompletion);
        Assert.Null(element.GetProperty<string>(MariaDbPropertyNames.Comment));
        // A one-shot event has no recurrence facets at all.
        Assert.Null(element.GetProperty<string>(MariaDbPropertyNames.IntervalValue));
        Assert.Null(element.GetProperty<string>(MariaDbPropertyNames.Starts));
    }

    [Fact]
    public async Task Event_RecordsNonDefaultStatusAndPreserve()
    {
        var element = await BuildEventAsync(
            "CREATE EVENT rollup ON SCHEDULE AT '2030-01-01 00:00:00' "
            + "ON COMPLETION PRESERVE DISABLE COMMENT 'nightly' DO SELECT 1;");

        Assert.Equal("DISABLED", element.GetRequiredProperty<string>(MariaDbPropertyNames.Status));
        Assert.True(element.GetRequiredProperty<bool>(MariaDbPropertyNames.PreserveOnCompletion));
        Assert.Equal("nightly", element.GetRequiredProperty<string>(MariaDbPropertyNames.Comment));
    }

    [Fact]
    public async Task Event_IsNotATrigger()
    {
        // An event is scheduled, not bound to a table, so it carries no trigger-table
        // relationship and does not collide with the trigger element type.
        var model = await BuildModelAsync(
            Tables + "CREATE EVENT rollup ON SCHEDULE AT '2030-01-01 00:00:00' DO SELECT 1;");

        Assert.DoesNotContain(model.Elements, i => i.Type == MariaDbElementTypes.SqlTrigger);
        var element = Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlEvent);
        Assert.Empty(element.Relationships);
    }

    [Fact]
    public async Task Event_SameDeclarationHashesEqual()
    {
        // Two builds of the same source must produce the same hash, or every deploy would
        // report drift.
        const string sql = "CREATE EVENT rollup ON SCHEDULE EVERY 1 DAY "
            + "STARTS '2030-01-01 00:00:00' DO INSERT INTO stats (n) VALUES (1);";

        var first = await BuildEventAsync(sql);
        var second = await BuildEventAsync(sql);

        Assert.True(HashUtility.HashesEqual(first.Hash, second.Hash));
    }

    [Fact]
    public async Task Event_DifferentScheduleHashesDiffer()
    {
        var first = await BuildEventAsync(
            "CREATE EVENT rollup ON SCHEDULE EVERY 1 DAY STARTS '2030-01-01 00:00:00' DO SELECT 1;");
        var second = await BuildEventAsync(
            "CREATE EVENT rollup ON SCHEDULE EVERY 2 DAY STARTS '2030-01-01 00:00:00' DO SELECT 1;");

        Assert.False(HashUtility.HashesEqual(first.Hash, second.Hash));
    }

    [Fact]
    public async Task Event_DuplicateNameIsAnError()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildModelAsync(
                Tables
                + "CREATE EVENT rollup ON SCHEDULE AT '2030-01-01 00:00:00' DO SELECT 1;\n"
                + "CREATE EVENT rollup ON SCHEDULE AT '2031-01-01 00:00:00' DO SELECT 2;"));

        Assert.Equal(SqlSourceException.DuplicateDefinition, ex.Code);
    }

    [Fact]
    public async Task Event_RecurringWithoutStartsIsAnError()
    {
        // Rejected as a source diagnostic rather than a raw exception, so the build reports it
        // against the offending file and line (issue #122).
        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildModelAsync(
                Tables + "CREATE EVENT rollup ON SCHEDULE EVERY 1 DAY DO SELECT 1;"));

        Assert.Contains("STARTS", ex.Message);
    }

    [Fact]
    public async Task Event_NonConstantExecuteAtIsAnError()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildModelAsync(
                Tables + "CREATE EVENT rollup ON SCHEDULE AT CURRENT_TIMESTAMP + INTERVAL 1 DAY "
                + "DO SELECT 1;"));

        Assert.Contains("non-constant AT", ex.Message);
    }

    [Fact]
    public async Task Event_NonConstantStartsIsAnError()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildModelAsync(
                Tables + "CREATE EVENT rollup ON SCHEDULE EVERY 1 DAY STARTS CURRENT_TIMESTAMP "
                + "DO SELECT 1;"));

        Assert.Contains("non-constant STARTS", ex.Message);
    }

    [Fact]
    public async Task Event_StartsWithIntervalOffsetIsAnError()
    {
        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildModelAsync(
                Tables + "CREATE EVENT rollup ON SCHEDULE EVERY 1 DAY "
                + "STARTS '2030-01-01 00:00:00' + INTERVAL 1 HOUR DO SELECT 1;"));

        Assert.Contains("non-constant STARTS", ex.Message);
    }

    [Fact]
    public async Task Event_ErrorIsReportedAtItsSourcePosition()
    {
        // The whole point of validating in the builder rather than the parser: the diagnostic
        // carries the file and line of the offending statement.
        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => BuildModelAsync(
                Tables + "CREATE EVENT rollup ON SCHEDULE EVERY 1 DAY DO SELECT 1;"));

        Assert.Equal(2, ex.Line);
        Assert.NotEmpty(ex.SourceFile);
    }
}

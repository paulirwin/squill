using Squill.PostgresParser.Syntax;
using Squill.TestFramework;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// Parser tests for <c>CREATE AGGREGATE</c> (issue #82). An aggregate arrives via the
/// <c>definestmt</c> grammar rule and captures its input type(s) plus the SFUNC/STYPE
/// definition items.
/// </summary>
public class CreateAggregateTests
{
    private static CreateAggregateStatement ParseOne(string text)
        => ParseAssertions.Single<CreateAggregateStatement>(new AntlrPostgresParser().Parse(text).Statements);

    [Fact]
    public void SimpleAggregate()
    {
        var stmt = ParseOne("""
            CREATE AGGREGATE group_concat(text) (
                SFUNC = _group_concat,
                STYPE = text
            );
            """);

        Assert.Equal("group_concat", stmt.Name.Segments[^1].Name);
        Assert.False(stmt.OrReplace);
        Assert.Equal("_group_concat", stmt.StateFunction);
        Assert.Equal("text", stmt.StateType!.TypeName);

        var parameter = Assert.Single(stmt.Parameters);
        Assert.Equal("text", parameter.DataType.TypeName);
    }

    [Fact]
    public void MultipleInputTypes()
    {
        var stmt = ParseOne("""
            CREATE AGGREGATE weighted_avg(numeric, numeric) (
                SFUNC = wavg_accum,
                STYPE = numeric[]
            );
            """);

        Assert.Equal(2, stmt.Parameters.Count);
        Assert.Equal("wavg_accum", stmt.StateFunction);
    }

    [Fact]
    public void OrReplaceAndSchemaQualifiedName()
    {
        var stmt = ParseOne("""
            CREATE OR REPLACE AGGREGATE app.my_agg(integer) (
                SFUNC = app.accum,
                STYPE = integer
            );
            """);

        Assert.True(stmt.OrReplace);
        Assert.Equal("app", stmt.Name.Segments[0].Name);
        Assert.Equal("my_agg", stmt.Name.Segments[^1].Name);
    }

    [Fact]
    public void AdditionalDefinitionItemsAreIgnored()
    {
        var stmt = ParseOne("""
            CREATE AGGREGATE sum_squares(numeric) (
                SFUNC = accum_sq,
                STYPE = numeric,
                INITCOND = '0'
            );
            """);

        Assert.Equal("accum_sq", stmt.StateFunction);
        Assert.Equal("numeric", stmt.StateType!.TypeName);
    }

    [Fact]
    public void SourcePositionIsRecorded()
    {
        var stmt = ParseOne("""


            CREATE AGGREGATE group_concat(text) (
                SFUNC = _group_concat,
                STYPE = text
            );
            """);

        Assert.Equal(3, stmt.Line);
        Assert.Equal(1, stmt.Column);
    }
}

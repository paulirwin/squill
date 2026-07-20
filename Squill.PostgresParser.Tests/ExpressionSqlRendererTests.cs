using Squill.PostgresParser;
using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

public class ExpressionSqlRendererTests
{
    // Parses a standalone predicate by wrapping it in a partial index and pulling the
    // WHERE clause back out, then rendering it to SQL.
    private static string RenderPredicate(string predicate)
    {
        var parser = new AntlrPostgresParser();
        var root = parser.Parse($"CREATE INDEX i ON t (c) WHERE {predicate};");
        var createIndex = Assert.IsType<CreateIndexStatement>(root.Statements[0]);
        Assert.NotNull(createIndex.WhereClause);
        return ExpressionSqlRenderer.Render(createIndex.WhereClause);
    }

    [Theory]
    [InlineData("email IS NOT NULL", "\"email\" IS NOT NULL")]
    [InlineData("email IS NULL", "\"email\" IS NULL")]
    [InlineData("active IS TRUE", "\"active\" IS TRUE")]
    [InlineData("active IS NOT FALSE", "\"active\" IS NOT FALSE")]
    [InlineData("status = 'active'", "\"status\" = 'active'")]
    [InlineData("qty > 0", "\"qty\" > 0")]
    [InlineData("qty >= 10", "\"qty\" >= 10")]
    [InlineData("status <> 'closed'", "\"status\" <> 'closed'")]
    [InlineData("a = 1 AND b = 2", "\"a\" = 1 AND \"b\" = 2")]
    [InlineData("a = 1 OR b = 2", "\"a\" = 1 OR \"b\" = 2")]
    [InlineData("(a = 1)", "(\"a\" = 1)")]
    public void Render_ProducesExpectedSql(string predicate, string expected)
    {
        Assert.Equal(expected, RenderPredicate(predicate));
    }
}

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

    /// <summary>
    /// Issue #140: the <c>func_expr_common_subexpr</c> forms must render back to valid SQL,
    /// since a CHECK or index predicate is carried into the model as text. Each keeps the
    /// keyword spelling it was written with rather than being rewritten into a call.
    /// </summary>
    [Theory]
    [InlineData("created_at < CURRENT_TIMESTAMP", "\"created_at\" < CURRENT_TIMESTAMP")]
    [InlineData("d < CURRENT_DATE", "\"d\" < CURRENT_DATE")]
    [InlineData("t < CURRENT_TIME(3)", "\"t\" < CURRENT_TIME(3)")]
    [InlineData("owner = CURRENT_USER", "\"owner\" = CURRENT_USER")]
    [InlineData("owner = SESSION_USER", "\"owner\" = SESSION_USER")]
    [InlineData("CAST(c AS integer) > 0", "CAST(\"c\" AS integer) > 0")]
    [InlineData("COALESCE(a, b) > 0", "COALESCE(\"a\", \"b\") > 0")]
    [InlineData("NULLIF(a, b) > 0", "NULLIF(\"a\", \"b\") > 0")]
    [InlineData("GREATEST(a, b) > 0", "GREATEST(\"a\", \"b\") > 0")]
    [InlineData("LEAST(a, b) > 0", "LEAST(\"a\", \"b\") > 0")]
    [InlineData("EXTRACT(YEAR FROM d) > 2000", "EXTRACT(YEAR FROM \"d\") > 2000")]
    [InlineData("SUBSTRING(c FROM 1 FOR 3) = 'abc'", "SUBSTRING(\"c\" FROM 1 FOR 3) = 'abc'")]
    [InlineData("SUBSTRING(c FROM 2) = 'ab'", "SUBSTRING(\"c\" FROM 2) = 'ab'")]
    [InlineData("TRIM(c) <> ''", "TRIM(BOTH \"c\") <> ''")]
    [InlineData("TRIM(LEADING 'x' FROM c) <> ''", "TRIM(LEADING 'x' FROM \"c\") <> ''")]
    [InlineData("POSITION('a' IN c) > 0", "POSITION('a' IN \"c\") > 0")]
    [InlineData("OVERLAY(c PLACING 'x' FROM 2) <> ''", "OVERLAY(\"c\" PLACING 'x' FROM 2) <> ''")]
    [InlineData("OVERLAY(c PLACING 'x' FROM 2 FOR 3) <> ''",
        "OVERLAY(\"c\" PLACING 'x' FROM 2 FOR 3) <> ''")]
    [InlineData("NORMALIZE(c, NFKC) <> ''", "NORMALIZE(\"c\", NFKC) <> ''")]
    [InlineData("COLLATION FOR (c) <> ''", "COLLATION FOR (\"c\") <> ''")]
    public void Render_CommonSubexpr_ProducesExpectedSql(string predicate, string expected)
    {
        Assert.Equal(expected, RenderPredicate(predicate));
    }
}

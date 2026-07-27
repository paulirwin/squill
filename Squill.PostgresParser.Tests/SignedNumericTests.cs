using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// A leading <c>-</c> or <c>+</c> on a numeric in <c>b_expr</c> position (issue #139). A column
/// <c>DEFAULT</c> and a <c>CHECK</c> both take that grammar path, so both were failing outright
/// on ordinary SQL such as <c>DEFAULT -5</c>.
/// </summary>
public class SignedNumericTests
{
    private static ColumnDefinition ParseSingleColumn(string columnSql)
    {
        var parser = new AntlrPostgresParser();

        var root = parser.Parse($"CREATE TABLE t (a {columnSql});");

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        return Assert.IsType<ColumnDefinition>(Assert.Single(createTable.Elements));
    }

    private static Expression DefaultExpressionOf(string columnSql)
    {
        var column = ParseSingleColumn(columnSql);

        var @default = Assert.IsType<DefaultColumnConstraint>(Assert.Single(column.Constraints));

        return @default.Expression;
    }

    [Theory]
    [InlineData("integer DEFAULT -5", 5L)]
    [InlineData("bigint DEFAULT -100", 100L)]
    public void Default_NegativeInteger_ParsesAsNegateUnary(string columnSql, long magnitude)
    {
        var unary = Assert.IsType<UnaryExpression>(DefaultExpressionOf(columnSql));

        Assert.Equal(PostgresBuiltInUnaryOperator.Negate, unary.Operator);

        var literal = Assert.IsType<LiteralExpression>(unary.Expression);

        Assert.Equal(magnitude, literal.Value);
    }

    [Fact]
    public void Default_NegativeNumeric_ParsesAsNegateUnary()
    {
        var unary = Assert.IsType<UnaryExpression>(DefaultExpressionOf("numeric DEFAULT -1.5"));

        Assert.Equal(PostgresBuiltInUnaryOperator.Negate, unary.Operator);

        var literal = Assert.IsType<LiteralExpression>(unary.Expression);

        Assert.Equal(1.5m, literal.Value);
    }

    [Fact]
    public void Default_PositiveSignedInteger_ParsesAsPlusUnary()
    {
        var unary = Assert.IsType<UnaryExpression>(DefaultExpressionOf("integer DEFAULT +5"));

        Assert.Equal(PostgresBuiltInUnaryOperator.Plus, unary.Operator);

        var literal = Assert.IsType<LiteralExpression>(unary.Expression);

        Assert.Equal(5L, literal.Value);
    }

    [Fact]
    public void Default_SignedValue_RendersBackToSql()
    {
        Assert.Equal("-5", ExpressionSqlRenderer.Render(DefaultExpressionOf("integer DEFAULT -5")));
        Assert.Equal("+5", ExpressionSqlRenderer.Render(DefaultExpressionOf("integer DEFAULT +5")));
    }

    [Fact]
    public void Check_WithNegativeLiteral_Parses()
    {
        var parser = new AntlrPostgresParser();

        var root = parser.Parse("CREATE TABLE t (a integer CHECK (a > -1));");

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        var column = Assert.IsType<ColumnDefinition>(Assert.Single(createTable.Elements));

        var check = Assert.IsType<CheckColumnConstraint>(Assert.Single(column.Constraints));

        Assert.Equal("\"a\" > -1", ExpressionSqlRenderer.Render(check.Expression));
    }
}

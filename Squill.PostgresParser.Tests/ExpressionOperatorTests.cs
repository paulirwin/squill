using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// Issue #141: the expression operators the visitor previously rejected with
/// <c>NotImplementedException</c> — <c>LIKE</c>/<c>ILIKE</c>/<c>SIMILAR TO</c>,
/// <c>BETWEEN</c>, <c>COLLATE</c>, <c>AT TIME ZONE</c>, the caret (<c>^</c>) operator,
/// a unary <c>qual_op</c>, and indirection on a parenthesized expression.
///
/// All of these are valid PostgreSQL in any expression position, so before this each one
/// failed the build outright. <c>LIKE</c> and <c>BETWEEN</c> matter most in practice: both
/// are common in <c>CHECK</c> constraints.
/// </summary>
public class ExpressionOperatorTests
{
    // Parses a standalone expression by wrapping it in a partial index's WHERE clause,
    // which is an a_expr position, and pulling the parsed expression back out.
    private static Expression ParseExpression(string expression)
    {
        var parser = new AntlrPostgresParser();
        var root = parser.Parse($"CREATE INDEX i ON t (c) WHERE {expression};");
        var createIndex = Assert.IsType<CreateIndexStatement>(root.Statements[0]);
        Assert.NotNull(createIndex.WhereClause);
        return createIndex.WhereClause;
    }

    [Theory]
    [InlineData("c LIKE 'a%'", PostgresBuiltInBinaryOperator.Like)]
    [InlineData("c NOT LIKE 'a%'", PostgresBuiltInBinaryOperator.NotLike)]
    [InlineData("c ILIKE 'a%'", PostgresBuiltInBinaryOperator.ILike)]
    [InlineData("c NOT ILIKE 'a%'", PostgresBuiltInBinaryOperator.NotILike)]
    [InlineData("c SIMILAR TO 'a%'", PostgresBuiltInBinaryOperator.SimilarTo)]
    [InlineData("c NOT SIMILAR TO 'a%'", PostgresBuiltInBinaryOperator.NotSimilarTo)]
    public void PatternMatch_ParsesToBinaryExpression(
        string sql, PostgresBuiltInBinaryOperator expected)
    {
        var binary = Assert.IsType<BinaryExpression>(ParseExpression(sql));

        Assert.Equal(expected, Assert.IsType<BuiltInOperator>(binary.Operator).Operator);
        Assert.Equal("c", Assert.IsType<ColumnReferenceExpression>(binary.Left).Identifier.Name);
        Assert.Equal("'a%'", Assert.IsType<LiteralExpression>(binary.Right).Text);
    }

    /// <summary>
    /// <c>ESCAPE</c> names the character that escapes a wildcard in the pattern. It changes
    /// what the predicate matches, so it must be carried rather than dropped.
    /// </summary>
    [Fact]
    public void PatternMatch_WithEscape_CarriesEscapeExpression()
    {
        var like = Assert.IsType<LikeExpression>(ParseExpression("c LIKE 'a!%b' ESCAPE '!'"));

        Assert.Equal(
            PostgresBuiltInBinaryOperator.Like,
            Assert.IsType<BuiltInOperator>(like.Operator).Operator);
        Assert.Equal("c", Assert.IsType<ColumnReferenceExpression>(like.Left).Identifier.Name);
        Assert.Equal("'a!%b'", Assert.IsType<LiteralExpression>(like.Right).Text);
        Assert.Equal("'!'", Assert.IsType<LiteralExpression>(like.Escape!).Text);
    }

    /// <summary>
    /// The grammar mis-associates <c>BETWEEN</c>: <c>c BETWEEN 1 AND 5</c> parses as
    /// <c>(c BETWEEN 1) AND 5</c>, with the upper bound landing as the right operand of the
    /// enclosing <c>a_expr_and</c> rather than inside the <c>BETWEEN</c> itself. The visitor
    /// has to reassociate, so this test is the one that pins the fix.
    /// </summary>
    [Theory]
    [InlineData("c BETWEEN 1 AND 5", false, false)]
    [InlineData("c NOT BETWEEN 1 AND 5", true, false)]
    [InlineData("c BETWEEN SYMMETRIC 1 AND 5", false, true)]
    [InlineData("c NOT BETWEEN SYMMETRIC 1 AND 5", true, true)]
    public void Between_ReassociatesUpperBound(string sql, bool negated, bool symmetric)
    {
        var between = Assert.IsType<BetweenExpression>(ParseExpression(sql));

        Assert.Equal(negated, between.IsNegated);
        Assert.Equal(symmetric, between.IsSymmetric);
        Assert.Equal("c", Assert.IsType<ColumnReferenceExpression>(between.Operand).Identifier.Name);
        Assert.Equal("1", Assert.IsType<LiteralExpression>(between.Lower).Text);
        Assert.Equal("5", Assert.IsType<LiteralExpression>(between.Upper).Text);
    }

    /// <summary>
    /// A BETWEEN nested inside a real AND must take only the bound that belongs to it and
    /// leave the rest of the conjunction alone — the reassociation must not swallow the
    /// whole right-hand side.
    /// </summary>
    [Fact]
    public void Between_InsideConjunction_TakesOnlyItsOwnUpperBound()
    {
        var and = Assert.IsType<BinaryExpression>(
            ParseExpression("c BETWEEN 1 AND 5 AND d = 2"));

        Assert.Equal(
            PostgresBuiltInBinaryOperator.And,
            Assert.IsType<BuiltInOperator>(and.Operator).Operator);

        var between = Assert.IsType<BetweenExpression>(and.Left);
        Assert.Equal("1", Assert.IsType<LiteralExpression>(between.Lower).Text);
        Assert.Equal("5", Assert.IsType<LiteralExpression>(between.Upper).Text);

        var comparison = Assert.IsType<BinaryExpression>(and.Right);
        Assert.Equal("d", Assert.IsType<ColumnReferenceExpression>(comparison.Left).Identifier.Name);
    }

    /// <summary>
    /// Two BETWEENs in one conjunction: each must claim exactly one bound from the chain.
    /// </summary>
    [Fact]
    public void Between_TwiceInConjunction_EachClaimsOneBound()
    {
        var and = Assert.IsType<BinaryExpression>(
            ParseExpression("c BETWEEN 1 AND 5 AND d BETWEEN 2 AND 6"));

        var left = Assert.IsType<BetweenExpression>(and.Left);
        Assert.Equal("1", Assert.IsType<LiteralExpression>(left.Lower).Text);
        Assert.Equal("5", Assert.IsType<LiteralExpression>(left.Upper).Text);

        var right = Assert.IsType<BetweenExpression>(and.Right);
        Assert.Equal("2", Assert.IsType<LiteralExpression>(right.Lower).Text);
        Assert.Equal("6", Assert.IsType<LiteralExpression>(right.Upper).Text);
    }

    [Fact]
    public void Collate_ParsesToCollateExpression()
    {
        var comparison = Assert.IsType<BinaryExpression>(ParseExpression("c COLLATE \"C\" > 'a'"));
        var collate = Assert.IsType<CollateExpression>(comparison.Left);

        Assert.Equal("c", Assert.IsType<ColumnReferenceExpression>(collate.Expression).Identifier.Name);
        Assert.Equal("C", Assert.Single(collate.Collation.Segments).Name);
    }

    [Fact]
    public void AtTimeZone_ParsesToAtTimeZoneExpression()
    {
        var comparison = Assert.IsType<BinaryExpression>(
            ParseExpression("c AT TIME ZONE 'UTC' > d"));
        var atTimeZone = Assert.IsType<AtTimeZoneExpression>(comparison.Left);

        Assert.Equal("c", Assert.IsType<ColumnReferenceExpression>(atTimeZone.Expression).Identifier.Name);
        Assert.Equal("'UTC'", Assert.IsType<LiteralExpression>(atTimeZone.TimeZone).Text);
    }

    [Fact]
    public void Caret_ParsesToExponentiation()
    {
        var comparison = Assert.IsType<BinaryExpression>(ParseExpression("c ^ 2 > 4"));
        var caret = Assert.IsType<BinaryExpression>(comparison.Left);

        Assert.Equal(
            PostgresBuiltInBinaryOperator.Exponentiation,
            Assert.IsType<BuiltInOperator>(caret.Operator).Operator);
        Assert.Equal("c", Assert.IsType<ColumnReferenceExpression>(caret.Left).Identifier.Name);
        Assert.Equal("2", Assert.IsType<LiteralExpression>(caret.Right).Text);
    }

    /// <summary>
    /// A prefix operator that is not one of the fixed sign tokens — here the absolute-value
    /// operator — reaches the visitor through <c>a_expr_unary_qualop</c>.
    /// </summary>
    [Fact]
    public void UnaryQualifiedOperator_ParsesToCustomUnaryExpression()
    {
        var comparison = Assert.IsType<BinaryExpression>(ParseExpression("@ c > 1"));
        var unary = Assert.IsType<CustomUnaryExpression>(comparison.Left);

        Assert.Equal("@", unary.Operator.Symbol);
        Assert.Equal("c", Assert.IsType<ColumnReferenceExpression>(unary.Expression).Identifier.Name);
    }

    /// <summary>
    /// A typed literal's type prefix is part of its meaning — <c>interval '1 day'</c> is not
    /// the string <c>'1 day'</c> — so it must be carried rather than dropped. Before this the
    /// prefix parsed but was silently discarded, which is worse than failing: the predicate
    /// would deploy meaning something else.
    /// </summary>
    [Theory]
    [InlineData("interval '1 day'", "interval", "'1 day'", null)]
    [InlineData("timestamp '2020-01-01'", "timestamp", "'2020-01-01'", null)]
    [InlineData("interval '1' DAY", "interval", "'1'", "DAY")]
    public void TypedLiteral_CarriesItsTypePrefix(
        string sql, string expectedType, string expectedLiteral, string? expectedModifier)
    {
        var comparison = Assert.IsType<BinaryExpression>(ParseExpression($"c > {sql}"));
        var typed = Assert.IsType<TypedLiteralExpression>(comparison.Right);

        Assert.Equal(expectedType, typed.TypeName);
        Assert.Equal(expectedLiteral, typed.Literal.Text);
        Assert.Equal(expectedModifier, typed.Modifier);
    }

    /// <summary>
    /// A multi-word type name must keep its spaces: <c>GetText()</c> concatenates tokens and
    /// would flatten this to <c>timestampwithtimezone</c>, which is not a type.
    /// </summary>
    [Fact]
    public void TypedLiteral_WithMultiWordTypeName_KeepsItsSpacing()
    {
        var comparison = Assert.IsType<BinaryExpression>(
            ParseExpression("c > timestamp with time zone '2020-01-01'"));
        var typed = Assert.IsType<TypedLiteralExpression>(comparison.Right);

        Assert.Equal("timestamp with time zone", typed.TypeName);
    }

    /// <summary>
    /// The constants that previously threw. Each is carried verbatim — Squill needs to
    /// reproduce a literal, never interpret it, and decoding the escape forms here would risk
    /// changing the value.
    /// </summary>
    [Theory]
    [InlineData("NULL", "NULL")]
    [InlineData("B'101'", "B'101'")]
    [InlineData("X'1f'", "X'1f'")]
    [InlineData("U&'d\\0061t'", "U&'d\\0061t'")]
    [InlineData("E'a\\nb'", "E'a\\nb'")]
    public void Constant_ParsesToLiteralCarryingItsSourceText(string sql, string expected)
    {
        var comparison = Assert.IsType<BinaryExpression>(ParseExpression($"c > {sql}"));

        Assert.Equal(expected, Assert.IsType<LiteralExpression>(comparison.Right).Text);
    }

    /// <summary>
    /// <c>(c).x</c> selects a field of a composite column. The parentheses are required —
    /// <c>c.x</c> would read <c>x</c> as a column of a table <c>c</c> — so they belong to the
    /// accessor rather than being grouping.
    /// </summary>
    [Theory]
    [InlineData("(c).x > 1", ".x")]
    [InlineData("(c)[1] > 1", "[1]")]
    [InlineData("(c)[1:2] > 1", "[1:2]")]
    public void Indirection_OnParenthesizedExpression_IsCarried(string sql, string expectedElement)
    {
        var comparison = Assert.IsType<BinaryExpression>(ParseExpression(sql));
        var indirection = Assert.IsType<IndirectionExpression>(comparison.Left);

        Assert.Equal(
            "c",
            Assert.IsType<ColumnReferenceExpression>(indirection.Expression).Identifier.Name);
        Assert.Equal(expectedElement, Assert.Single(indirection.Elements));
    }
}

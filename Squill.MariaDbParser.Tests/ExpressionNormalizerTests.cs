using Squill.MariaDbParser;

namespace Squill.MariaDbParser.Tests;

/// <summary>
/// Issue #156: a CHECK predicate and a generated column's expression must take part in their
/// element's identity, so redefining one is deployed rather than silently ignored. That needs a
/// canonical form both spellings reduce to, because both engines rewrite the expression text they
/// are given when they store it.
///
/// Unlike PostgreSQL, MariaDB and MySQL rewrite only LEXICALLY — the structure of the expression
/// survives — so normalizing tokens is enough here and no expression tree is needed. Measured
/// against live servers, the differences are:
///
/// <list type="bullet">
/// <item>identifiers come back backtick-quoted (<c>price</c> → <c>`price`</c>);</item>
/// <item>keywords and operators come back lower-cased (<c>AND</c> → <c>and</c>);</item>
/// <item>MySQL wraps the whole predicate in parentheses, MariaDB does not;</item>
/// <item>MySQL prefixes a string literal with its charset introducer
///   (<c>'a%'</c> → <c>_latin1\'a%\'</c>);</item>
/// <item>whitespace is re-spaced around operators and after commas.</item>
/// </list>
///
/// Every expected form below was MEASURED against a live mariadb:latest and mysql:latest, never
/// inferred from the grammar. A rule that is wrong here is worse than a missing one: it makes an
/// unchanged predicate look changed, and the object is redeployed on every deploy forever.
/// </summary>
public class ExpressionNormalizerTests
{
    private static string? Normalize(string expression)
        => ExpressionNormalizer.TryNormalize(expression, out var canonical) ? canonical : null;

    /// <summary>
    /// The core contract: a predicate as DECLARED, as MariaDB stores it, and as MySQL stores it
    /// must all reduce to one canonical token.
    /// </summary>
    [Theory]
    // declared, MariaDB CHECK_CLAUSE, MySQL CHECK_CLAUSE
    [InlineData("price > 0", "`price` > 0", "(`price` > 0)")]
    [InlineData("quantity > 0 AND quantity < 100",
        "`quantity` > 0 and `quantity` < 100",
        "((`quantity` > 0) and (`quantity` < 100))")]
    [InlineData("price BETWEEN 1 AND 5",
        "`price` between 1 and 5",
        "(`price` between 1 and 5)")]
    [InlineData("quantity IN (1,2,3)",
        "`quantity` in (1,2,3)",
        "(`quantity` in (1,2,3))")]
    [InlineData("price * quantity", "`price` * `quantity`", "(`price` * `quantity`)")]
    // Both engines add precedence-clarifying parentheses around ARITHMETIC, not just around
    // boolean operands, so the redundant-parenthesis rule has to know arithmetic precedence too.
    [InlineData("celsius * 9 / 5 + 32",
        "`celsius` * 9 / 5 + 32",
        "(((`celsius` * 9) / 5) + 32)")]
    [InlineData("name IS NOT NULL", "`name` is not null", "(`name` is not null)")]
    public void DeclaredAndExtracted_NormalizeToTheSameToken(
        string declared, string mariaDb, string mySql)
    {
        var declaredCanonical = Normalize(declared);

        Assert.NotNull(declaredCanonical);
        Assert.Equal(declaredCanonical, Normalize(mariaDb));
        Assert.Equal(declaredCanonical, Normalize(mySql));
    }

    /// <summary>
    /// MySQL prefixes a string literal with the connection charset's introducer and escapes the
    /// quotes. The introducer names how the literal is interpreted, not what the predicate tests,
    /// so it must not make an unchanged predicate look changed.
    /// </summary>
    [Theory]
    [InlineData("name LIKE 'a%'", "`name` like 'a%'", @"(`name` like _latin1\'a%\')")]
    [InlineData("name = 'x'", "`name` = 'x'", @"(`name` = _latin1\'x\')")]
    public void CharsetIntroducer_IsNormalizedAway(string declared, string mariaDb, string mySql)
    {
        var declaredCanonical = Normalize(declared);

        Assert.NotNull(declaredCanonical);
        Assert.Equal(declaredCanonical, Normalize(mariaDb));
        Assert.Equal(declaredCanonical, Normalize(mySql));
    }

    /// <summary>
    /// The other half of the contract: predicates that genuinely differ must NOT collapse
    /// together, or a real change would still be missed — the very bug being fixed.
    /// </summary>
    [Theory]
    [InlineData("price > 0", "price > 10")]
    [InlineData("price > 0", "price >= 0")]
    [InlineData("price > 0", "quantity > 0")]
    [InlineData("quantity IN (1,2)", "quantity IN (1,2,3)")]
    [InlineData("price BETWEEN 1 AND 5", "price BETWEEN 1 AND 6")]
    [InlineData("name LIKE 'a%'", "name LIKE 'b%'")]
    [InlineData("a > 0 AND b > 0", "a > 0 OR b > 0")]
    [InlineData("price * quantity", "price - quantity")]
    public void DifferentPredicates_NormalizeDifferently(string left, string right)
    {
        var leftCanonical = Normalize(left);
        var rightCanonical = Normalize(right);

        Assert.NotNull(leftCanonical);
        Assert.NotNull(rightCanonical);
        Assert.NotEqual(rightCanonical, leftCanonical);
    }

    /// <summary>
    /// Identifier case is significant on some platforms and not others, but the engines report an
    /// identifier with the case it was declared in, so normalization must preserve it — folding
    /// it would merge two genuinely distinct columns.
    /// </summary>
    [Fact]
    public void IdentifierCase_IsPreserved()
    {
        Assert.NotEqual(Normalize("Price > 0"), Normalize("price > 0"));
    }

    /// <summary>
    /// Normalization is idempotent: a canonical form fed back through is unchanged. Without this
    /// a predicate could oscillate between two spellings and redeploy forever.
    /// </summary>
    [Theory]
    [InlineData("price > 0")]
    [InlineData("price BETWEEN 1 AND 5")]
    [InlineData("quantity IN (1,2,3)")]
    [InlineData("name LIKE 'a%'")]
    [InlineData("price * quantity")]
    public void Normalization_IsIdempotent(string expression)
    {
        var once = Normalize(expression);
        Assert.NotNull(once);
        Assert.Equal(once, Normalize(once));
    }

    /// <summary>
    /// Text the normalizer cannot tokenize must report failure rather than guess, so the caller
    /// leaves the property out of the identity hash — degrading to the known gap in issue #156
    /// rather than to a false "changed" that redeploys the object forever.
    /// </summary>
    /// <remarks>
    /// This works at the token level, so it rejects what cannot be LEXED (an unterminated
    /// literal), not what merely fails to parse. An incomplete but well-formed token sequence
    /// such as <c>price &gt;</c> normalizes fine; it never reaches here, because the engines only
    /// ever report a predicate they already accepted.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("name = 'unterminated")]
    public void UnnormalizableExpression_ReportsFailure(string expression)
    {
        Assert.Null(Normalize(expression));
    }

    /// <summary>
    /// Redundant grouping is removed, but grouping that decides how the predicate parses is not:
    /// dropping the parentheses from <c>(a OR b) AND c</c> would silently turn it into
    /// <c>a OR (b AND c)</c>, making two different predicates compare equal and hiding a real
    /// change — the very bug being fixed.
    /// </summary>
    [Fact]
    public void PrecedenceChangingParentheses_AreNotStripped()
    {
        Assert.NotEqual(Normalize("(a OR b) AND c"), Normalize("a OR b AND c"));
        Assert.NotEqual(Normalize("(a AND b) OR c"), Normalize("a AND (b OR c)"));

        // The same for arithmetic: (a + b) * c is not a + b * c.
        Assert.NotEqual(Normalize("(a + b) * c"), Normalize("a + b * c"));
        Assert.NotEqual(Normalize("a / (b * c)"), Normalize("a / b * c"));

        // The redundant case still collapses: MySQL parenthesizes each operand of a boolean.
        Assert.Equal(Normalize("a > 0 AND b < 1"), Normalize("((a > 0) and (b < 1))"));
    }
}

using Squill.PostgresParser;
using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// Issue #156: a CHECK predicate and a generated column's expression must take part in their
/// element's identity, so redefining one is deployed rather than silently ignored. That needs a
/// canonical form both spellings reduce to, because PostgreSQL rewrites the expression text it
/// is given when it stores it.
///
/// The rewrite is not lexical, so no amount of paren-stripping bridges it: PostgreSQL desugars
/// <c>BETWEEN</c> into a pair of comparisons, spells <c>LIKE</c> as <c>~~</c>, turns
/// <c>IN (…)</c> into <c>= ANY (ARRAY[…])</c>, and injects casts onto literals. It is, however,
/// a fixed point — feeding it its own output back reproduces it — so the canonical form is
/// "what the engine would have stored".
///
/// Every expected extracted form below was MEASURED against a live postgres:latest, never
/// inferred from the grammar. A rule that is wrong here is worse than a missing one: it makes an
/// unchanged predicate look changed, and the object is redeployed on every deploy forever.
/// </summary>
public class ExpressionNormalizerTests
{
    // Parses a standalone predicate by wrapping it in a partial index and pulling the WHERE
    // clause back out, matching ExpressionSqlRendererTests' approach.
    private static Expression Parse(string predicate)
    {
        var parser = new AntlrPostgresParser();
        var root = parser.Parse($"CREATE INDEX i ON t (c) WHERE {predicate};");
        var createIndex = Assert.IsType<CreateIndexStatement>(root.Statements[0]);
        Assert.NotNull(createIndex.WhereClause);
        return createIndex.WhereClause;
    }

    private static string? Normalize(string predicate)
        => ExpressionNormalizer.TryNormalize(Parse(predicate), out var canonical) ? canonical : null;

    /// <summary>
    /// The core contract. Each row is a predicate as DECLARED in source, paired with the exact
    /// text <c>pg_get_constraintdef</c> returned for it on a live server. Both must reduce to
    /// the same canonical token, or an unchanged predicate re-diffs on every deploy.
    /// </summary>
    [Theory]
    // Injected casts on literals.
    [InlineData("price > 0", "(price > (0)::numeric)")]
    [InlineData("name <> ''", "(name <> ''::text)")]
    [InlineData("upper(name) = 'X'", "(upper(name) = 'X'::text)")]
    // No rewrite at all beyond parens.
    [InlineData("quantity >= 1", "(quantity >= 1)")]
    [InlineData("name IS NOT NULL", "(name IS NOT NULL)")]
    [InlineData("flag", "flag")]
    [InlineData("NOT flag", "(NOT flag)")]
    [InlineData("length(name) > 2", "(length(name) > 2)")]
    [InlineData("quantity % 2 = 0", "((quantity % 2) = 0)")]
    // LIKE is stored as the operator it desugars to.
    [InlineData("name LIKE 'a%'", "(name ~~ 'a%'::text)")]
    [InlineData("name NOT LIKE 'a%'", "(name !~~ 'a%'::text)")]
    [InlineData("name ~ '^a'", "(name ~ '^a'::text)")]
    // BETWEEN is desugared into comparisons.
    [InlineData("price BETWEEN 1 AND 5",
        "((price >= (1)::numeric) AND (price <= (5)::numeric))")]
    [InlineData("price NOT BETWEEN 1 AND 5",
        "((price < (1)::numeric) OR (price > (5)::numeric))")]
    // Nesting and precedence.
    [InlineData("price > 0 AND quantity > 0 OR flag",
        "(((price > (0)::numeric) AND (quantity > 0)) OR flag)")]
    // A cast written in the SOURCE is not engine noise and must survive normalization, so it
    // still distinguishes two genuinely different predicates.
    [InlineData("price::integer > 0", "((price)::integer > 0)")]
    public void DeclaredAndExtracted_NormalizeToTheSameToken(string declared, string extracted)
    {
        var declaredCanonical = Normalize(declared);
        var extractedCanonical = Normalize(extracted);

        Assert.NotNull(declaredCanonical);
        Assert.NotNull(extractedCanonical);
        Assert.Equal(extractedCanonical, declaredCanonical);
    }

    /// <summary>
    /// The other half of the contract: predicates that genuinely differ must NOT collapse
    /// together, or a real change would still be missed — the very bug being fixed.
    /// </summary>
    [Theory]
    [InlineData("price > 0", "price > 10")]
    [InlineData("price > 0", "price >= 0")]
    [InlineData("price > 0", "price < 0")]
    [InlineData("price BETWEEN 1 AND 5", "price BETWEEN 1 AND 6")]
    [InlineData("name LIKE 'a%'", "name LIKE 'b%'")]
    [InlineData("name LIKE 'a%'", "name NOT LIKE 'a%'")]
    [InlineData("a > 0 AND b > 0", "a > 0 OR b > 0")]
    [InlineData("price > 0", "quantity > 0")]
    public void DifferentPredicates_NormalizeDifferently(string left, string right)
    {
        var leftCanonical = Normalize(left);
        var rightCanonical = Normalize(right);

        Assert.NotNull(leftCanonical);
        Assert.NotNull(rightCanonical);
        Assert.NotEqual(rightCanonical, leftCanonical);
    }

    /// <summary>
    /// Normalization is idempotent: a canonical form fed back through is unchanged. Without
    /// this a predicate could oscillate between two spellings and redeploy forever.
    /// </summary>
    [Theory]
    [InlineData("price > 0")]
    [InlineData("price BETWEEN 1 AND 5")]
    [InlineData("name LIKE 'a%'")]
    public void Normalization_IsIdempotent(string predicate)
    {
        var once = Normalize(predicate);
        Assert.NotNull(once);
        Assert.Equal(once, Normalize(once));
    }

    /// <summary>
    /// An expression the normalizer cannot reduce must report failure rather than guess. The
    /// caller then leaves the property out of the identity hash — degrading to the known gap in
    /// issue #156 rather than to a false "changed" that redeploys the object forever.
    /// </summary>
    /// <remarks>
    /// <c>BETWEEN SYMMETRIC</c> is the example because its rewrite is genuinely more than the
    /// plain form's: measured on a live server, <c>price BETWEEN SYMMETRIC 5 AND 1</c> is stored
    /// as a four-way disjunction covering both bound orderings. Rather than encode that from
    /// one measurement, the normalizer refuses it.
    /// </remarks>
    [Fact]
    public void UnnormalizableExpression_ReportsFailure()
    {
        Assert.Null(Normalize("price BETWEEN SYMMETRIC 5 AND 1"));
    }

    /// <summary>
    /// A custom or extension-defined operator has no rewrite to reverse — PostgreSQL stores it
    /// as written — so it normalizes by passing through, and two different ones stay distinct.
    /// This is also the path <c>~~</c> takes, since <c>LIKE</c>'s underlying operator arrives as
    /// a custom one.
    /// </summary>
    [Fact]
    public void CustomOperator_PassesThrough()
    {
        Assert.Equal(Normalize("(c OPERATOR(public.===) 1)"), Normalize("c OPERATOR(public.===) 1"));
        Assert.NotEqual(Normalize("c OPERATOR(public.===) 1"), Normalize("c OPERATOR(public.==) 1"));
    }

    /// <summary>
    /// <c>IN (…)</c> is stored by PostgreSQL as <c>= ANY (ARRAY[…])</c>, so normalizing the two
    /// together is needed for a CHECK that uses one. Neither side parses today: <c>IN</c> in an
    /// expression position fails outright, and <c>= ANY (…)</c> hits an unimplemented visitor
    /// branch (<c>PostgresVisitor.AExprCompare.cs</c>: "Subquery_op not yet supported"). Both are
    /// pre-existing parser gaps rather than normalizer ones.
    /// </summary>
    [Fact(Skip = "Blocked by issue #170: IN (…) does not parse in an expression position, and "
                 + "= ANY (ARRAY[…]) hits an unimplemented Subquery_op visitor branch.")]
    public void InList_NormalizesWithAnyArray()
    {
        Assert.Equal(
            Normalize("(quantity = ANY (ARRAY[1, 2, 3]))"),
            Normalize("quantity IN (1,2,3)"));
    }
}

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
    // A signed numeric constant is stored as a QUOTED literal carrying the sign.
    [InlineData("x > -1", "(x > '-1'::integer)")]
    // Comparing an integer column against a fractional constant also widens the COLUMN, so this
    // row exercises the signed literal and the injected column cast together.
    [InlineData("x > -1.5", "((x)::numeric > '-1.5'::numeric)")]
    // Nesting and precedence.
    [InlineData("price > 0 AND quantity > 0 OR flag",
        "(((price > (0)::numeric) AND (quantity > 0)) OR flag)")]
    // A cast on a column is erased, because PostgreSQL stores a written one and an inferred one
    // identically: a declared `quantity::numeric > 0` and the widening it infers for
    // `price * quantity` both come back as `(quantity)::numeric`. Since the engine cannot tell
    // them apart, neither can a canonical form built from what it reports.
    [InlineData("price::integer > 0", "((price)::integer > 0)")]
    [InlineData("price * quantity", "(price * (quantity)::numeric)")]
    // IN is desugared into a quantified comparison against an array (issue #170). Measured on
    // postgres:latest: IN becomes `= ANY`, and NOT IN becomes `<> ALL` rather than a negated
    // ANY — so the two quantifiers are not interchangeable and both must normalize.
    [InlineData("quantity IN (1, 2, 3)", "(quantity = ANY (ARRAY[1, 2, 3]))")]
    [InlineData("quantity NOT IN (1, 2)", "(quantity <> ALL (ARRAY[1, 2]))")]
    [InlineData("name IN ('a', 'b')", "(name = ANY (ARRAY['a'::text, 'b'::text]))")]
    // SOME is a synonym for ANY and is stored as ANY, so the two spellings must agree.
    [InlineData("quantity = SOME (ARRAY[1, 2])", "(quantity = ANY (ARRAY[1, 2]))")]
    // A single-element IN does not become an array at all — it collapses to a plain
    // comparison, so the declared IN must reduce to exactly what the extracted form is.
    [InlineData("quantity IN (1)", "(quantity = 1)")]
    [InlineData("quantity NOT IN (5)", "(quantity <> 5)")]
    [InlineData("name IN ('x')", "(name = 'x'::text)")]
    // Any operator can be quantified, not just equality.
    [InlineData("quantity > ANY (ARRAY[1, 2])", "(quantity > ANY (ARRAY[1, 2]))")]
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
    [Theory]
    [InlineData("price BETWEEN SYMMETRIC 5 AND 1")]
    // A LIKE with an ESCAPE is stored as a call to the internal like_escape() function
    // (`code ~~ like_escape('%!%%'::text, '!'::text)`) rather than the LIKE … ESCAPE spelling.
    // Encoding that would be guessing at an implementation detail, so it is refused (issue #171).
    [InlineData("code LIKE '%!%%' ESCAPE '!'")]
    // COLLATE has no measured canonical form.
    [InlineData("code COLLATE \"C\" > 'a'")]
    public void UnnormalizableExpression_ReportsFailure(string predicate)
    {
        Assert.Null(Normalize(predicate));
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
    /// together is needed for a CHECK that uses one (issue #170). Both spellings now parse: the
    /// <c>in_expr</c> alternatives are mapped, and the <c>subquery_Op</c> branch of
    /// <c>a_expr_compare</c> is implemented.
    /// </summary>
    [Fact]
    public void InList_NormalizesWithAnyArray()
    {
        Assert.Equal(
            Normalize("(quantity = ANY (ARRAY[1, 2, 3]))"),
            Normalize("quantity IN (1,2,3)"));
    }

    /// <summary>
    /// The quantifier is part of the meaning, so <c>ANY</c> and <c>ALL</c> must not collapse
    /// together — <c>= ANY</c> is membership while <c>= ALL</c> requires every element to
    /// match. Nor may a differing element list collapse.
    /// </summary>
    [Theory]
    [InlineData("quantity = ANY (ARRAY[1, 2])", "quantity = ALL (ARRAY[1, 2])")]
    [InlineData("quantity IN (1, 2)", "quantity NOT IN (1, 2)")]
    [InlineData("quantity IN (1, 2)", "quantity IN (1, 3)")]
    [InlineData("quantity IN (1, 2)", "quantity IN (1, 2, 3)")]
    [InlineData("quantity IN (1, 2)", "name IN (1, 2)")]
    public void QuantifiedComparisons_ThatDiffer_NormalizeDifferently(string a, string b)
    {
        var canonicalA = Normalize(a);
        var canonicalB = Normalize(b);

        Assert.NotNull(canonicalA);
        Assert.NotNull(canonicalB);
        Assert.NotEqual(canonicalB, canonicalA);
    }

    /// <summary>
    /// The element order in an <c>IN</c> list is preserved rather than sorted: PostgreSQL keeps
    /// the order it was given (measured), so re-ordering the list is a real change to the
    /// stored predicate and must not compare equal.
    /// </summary>
    [Fact]
    public void InList_ElementOrder_IsSignificant()
    {
        Assert.NotEqual(Normalize("quantity IN (2, 1)"), Normalize("quantity IN (1, 2)"));
    }
}

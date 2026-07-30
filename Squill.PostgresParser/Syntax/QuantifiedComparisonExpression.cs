namespace Squill.PostgresParser.Syntax;

/// <summary>
/// The quantifier of a comparison against a set — the <c>ANY</c> / <c>SOME</c> / <c>ALL</c> of
/// <c>x = ANY (ARRAY[…])</c>.
///
/// <para>
/// <c>SOME</c> is not a member: PostgreSQL treats it as a synonym for <c>ANY</c> and stores it
/// as <c>ANY</c> (measured), so keeping it distinct would make the two spellings of one
/// predicate normalize differently and re-diff on every deploy.
/// </para>
/// </summary>
public enum ComparisonQuantifier
{
    Any,
    All,
}

/// <summary>
/// A comparison quantified over a set — <c>x = ANY (ARRAY[1, 2])</c>, <c>x &lt;&gt; ALL (…)</c>.
///
/// <para>
/// This is the form PostgreSQL stores an <c>IN</c> predicate as, which is why it matters for
/// the expression normalizer (issue #170). Measured on <c>postgres:latest</c>:
/// <c>q IN (1, 2)</c> is stored as <c>q = ANY (ARRAY[1, 2])</c> and <c>q NOT IN (1, 2)</c> as
/// <c>q &lt;&gt; ALL (ARRAY[1, 2])</c> — a negated <c>ANY</c> is <em>not</em> what it becomes,
/// so the operator and the quantifier both carry meaning.
/// </para>
///
/// <para>
/// The right operand is an ordinary <see cref="Expression"/> rather than an array specifically,
/// because the grammar admits any parenthesized expression there (and a subquery, which is not
/// mapped).
/// </para>
/// </summary>
public class QuantifiedComparisonExpression(
    Expression left,
    Operator op,
    ComparisonQuantifier quantifier,
    Expression right) : Expression
{
    public Expression Left { get; } = left;

    public Operator Operator { get; } = op;

    public ComparisonQuantifier Quantifier { get; } = quantifier;

    public Expression Right { get; } = right;
}

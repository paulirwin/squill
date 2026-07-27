namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A pattern-match predicate — <c>LIKE</c>, <c>ILIKE</c> or <c>SIMILAR TO</c>, each
/// optionally negated (issue #141).
///
/// It derives from <see cref="BinaryExpression"/> so the ordinary binary-expression handling
/// applies; it exists only to carry the optional <c>ESCAPE</c> operand, which names the
/// character that escapes a wildcard in the pattern and therefore changes what the predicate
/// matches. When no <c>ESCAPE</c> is written a plain <see cref="BinaryExpression"/> is used
/// instead, so consumers that do not care about <c>ESCAPE</c> need no special case.
/// </summary>
public class LikeExpression : BinaryExpression
{
    public LikeExpression(
        Expression left,
        PostgresBuiltInBinaryOperator op,
        Expression right,
        Expression? escape)
        : base(left, new BuiltInOperator(op), right)
    {
        Escape = escape;
    }

    /// <summary>
    /// The <c>ESCAPE</c> character expression, or <c>null</c> when none was written.
    /// </summary>
    public Expression? Escape { get; }
}

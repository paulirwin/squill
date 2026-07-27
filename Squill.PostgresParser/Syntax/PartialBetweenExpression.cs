namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>BETWEEN</c> whose upper bound has not been attached yet — an internal parse-time
/// artifact, never part of a finished syntax tree (issue #141).
///
/// The grammar's <c>a_expr_like</c> rule takes only one right operand, so it sees
/// <c>c BETWEEN 1</c> and the <c>AND 5</c> is left to the enclosing <c>a_expr_and</c>. The
/// like visitor emits this node to carry the lower bound and the modifiers up one level,
/// where <c>VisitA_expr_and</c> pairs it with the next conjunct and produces a finished
/// <see cref="BetweenExpression"/>.
///
/// If one of these ever escapes into a completed tree it means a <c>BETWEEN</c> was written
/// without its <c>AND</c>, which the visitor reports as a parse error rather than letting an
/// incomplete node reach the model.
/// </summary>
internal class PartialBetweenExpression : Expression
{
    public PartialBetweenExpression(
        Expression operand,
        Expression lower,
        bool isNegated,
        bool isSymmetric)
    {
        Operand = operand;
        Lower = lower;
        IsNegated = isNegated;
        IsSymmetric = isSymmetric;
    }

    public Expression Operand { get; }

    public Expression Lower { get; }

    public bool IsNegated { get; }

    public bool IsSymmetric { get; }

    /// <summary>Completes this into a real <see cref="BetweenExpression"/>.</summary>
    public BetweenExpression WithUpper(Expression upper)
        => new(Operand, Lower, upper, IsNegated, IsSymmetric);
}

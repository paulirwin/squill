namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>expr [NOT] BETWEEN [SYMMETRIC] lower AND upper</c> range predicate (issue #141).
///
/// Note the grammar does not produce this shape directly: <c>a_expr_like</c> admits only a
/// single right operand, so <c>c BETWEEN 1 AND 5</c> parses as <c>(c BETWEEN 1) AND 5</c>
/// with the upper bound landing on the enclosing <c>a_expr_and</c>. The visitor reassociates
/// the two halves into this node — see <c>PostgresVisitor.AExprAnd.cs</c>.
/// </summary>
public class BetweenExpression : Expression
{
    public BetweenExpression(
        Expression operand,
        Expression lower,
        Expression upper,
        bool isNegated,
        bool isSymmetric)
    {
        Operand = operand;
        Lower = lower;
        Upper = upper;
        IsNegated = isNegated;
        IsSymmetric = isSymmetric;
    }

    public Expression Operand { get; }

    public Expression Lower { get; }

    public Expression Upper { get; }

    /// <summary>True when written as <c>NOT BETWEEN</c>.</summary>
    public bool IsNegated { get; }

    /// <summary>
    /// True when written as <c>BETWEEN SYMMETRIC</c>, which matches regardless of which
    /// bound is the larger.
    /// </summary>
    public bool IsSymmetric { get; }
}

namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>COLLATION FOR (expr)</c> (issue #140), which yields the collation name of its
/// operand. The parentheses are part of the syntax rather than a nested
/// <see cref="ParenthesizedExpression"/>, so only the inner expression is carried.
/// </summary>
public class CollationForExpression : Expression
{
    public CollationForExpression(Expression expression)
    {
        Expression = expression;
    }

    public Expression Expression { get; }
}

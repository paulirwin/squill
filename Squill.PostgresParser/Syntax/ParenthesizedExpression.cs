namespace Squill.PostgresParser.Syntax;

public class ParenthesizedExpression : Expression
{
    public ParenthesizedExpression(Expression expression)
    {
        Expression = expression;
    }

    public Expression Expression { get; }
}
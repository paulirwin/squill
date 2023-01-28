namespace Squill.PostgresParser.Syntax;

public class UnaryExpression : Expression
{
    public UnaryExpression(PostgresBuiltInUnaryOperator op, Expression expression)
    {
        Operator = op;
        Expression = expression;
    }

    public PostgresBuiltInUnaryOperator Operator { get; }
    
    public Expression Expression { get; }
}
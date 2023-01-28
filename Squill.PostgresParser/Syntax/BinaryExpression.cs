namespace Squill.PostgresParser.Syntax;

public class BinaryExpression : Expression
{
    public BinaryExpression(Expression left, Operator op, Expression right)
    {
        Left = left;
        Operator = op;
        Right = right;
    }

    public Expression Left { get; }
    
    public Operator Operator { get; }
    
    public Expression Right { get; }
}
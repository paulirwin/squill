namespace Squill.PostgresParser.Syntax;

public class TypecastExpression : Expression
{
    public TypecastExpression(Expression expression, DataType dataType)
    {
        Expression = expression;
        DataType = dataType;
    }

    public Expression Expression { get; }
    
    public DataType DataType { get; }
}
namespace Squill.PostgresParser.Syntax;

public class IndexElement : SyntaxNode
{
    public IndexElement(Expression expression, 
        IndexElementDirection? direction, 
        IndexElementNullOrder? nullOrder)
    {
        Expression = expression;
        Direction = direction;
        NullOrder = nullOrder;
    }

    public Expression Expression { get; }

    public IndexElementDirection? Direction { get; }
    
    public IndexElementNullOrder? NullOrder { get; }
}
namespace Squill.PostgresParser.Syntax;

public class IndexElement : SyntaxNode
{
    public IndexElement(Expression expression,
        IndexElementDirection? direction,
        IndexElementNullOrder? nullOrder,
        Identifier? operatorClass = null)
    {
        Expression = expression;
        Direction = direction;
        NullOrder = nullOrder;
        OperatorClass = operatorClass;
    }

    public Expression Expression { get; }

    public IndexElementDirection? Direction { get; }

    public IndexElementNullOrder? NullOrder { get; }

    /// <summary>
    /// The operator class for this index element (e.g. <c>vector_cosine_ops</c> on an
    /// HNSW index). Null when the default operator class for the type is used.
    /// </summary>
    public Identifier? OperatorClass { get; }
}
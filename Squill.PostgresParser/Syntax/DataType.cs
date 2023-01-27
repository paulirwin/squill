namespace Squill.PostgresParser.Syntax;

public abstract class DataType : SyntaxNode
{
    protected DataType(string typeName)
    {
        TypeName = typeName;
    }

    public string TypeName { get; }

    public IList<Expression> Modifiers { get; } = new List<Expression>();
}
namespace Squill.PostgresParser.Syntax;

public class DataType : SyntaxNode
{
    public DataType(string typeName)
    {
        TypeName = typeName;
    }

    public string TypeName { get; }

    public IList<object?> Modifiers { get; } = new List<object?>();
}
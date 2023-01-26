namespace Squill.PostgresParser.Syntax;

public class ColumnDefinition : SyntaxNode, ITableElement
{
    public ColumnDefinition(string name, DataType dataType)
    {
        Name = name;
        DataType = dataType;
    }

    public string Name { get; }
    
    public DataType DataType { get; }

    public IList<ColumnConstraint> Constraints { get; } = new List<ColumnConstraint>();
}
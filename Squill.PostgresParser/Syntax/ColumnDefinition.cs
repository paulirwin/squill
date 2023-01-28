namespace Squill.PostgresParser.Syntax;

public class ColumnDefinition : SyntaxNode, ITableElement
{
    public ColumnDefinition(Identifier name, DataType dataType)
    {
        Name = name;
        DataType = dataType;
    }

    public Identifier Name { get; }
    
    public DataType DataType { get; }

    public IList<ColumnConstraint> Constraints { get; } = new List<ColumnConstraint>();
}
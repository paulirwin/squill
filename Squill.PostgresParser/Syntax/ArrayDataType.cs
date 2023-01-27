namespace Squill.PostgresParser.Syntax;

public class ArrayDataType : DataType
{
    public ArrayDataType(string typeName, DataType elementType, int? size = null) 
        : base(typeName)
    {
        ElementType = elementType;
        Size = size;
    }

    public DataType ElementType { get; }
    
    public int? Size { get; }
}
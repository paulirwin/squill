namespace Squill.PostgresParser.Syntax;

public class ObjectIdentifierTypeName : DataType
{
    public ObjectIdentifierTypeName(string typeName, PostgresObjectIdentifierTypes objectIdentifierType) 
        : base(typeName)
    {
        ObjectIdentifierType = objectIdentifierType;
    }

    public PostgresObjectIdentifierTypes ObjectIdentifierType { get; }
}
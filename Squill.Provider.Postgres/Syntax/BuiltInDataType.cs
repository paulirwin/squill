namespace Squill.Provider.Postgres.Syntax;

public class BuiltInDataType : DataType
{
    public BuiltInDataType(PostgresBuiltInDataType type, string originalText)
        : base(originalText)
    {
        Type = type;
    }

    public PostgresBuiltInDataType Type { get; }
}
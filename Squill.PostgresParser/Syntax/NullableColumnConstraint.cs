namespace Squill.PostgresParser.Syntax;

public class NullableColumnConstraint : ColumnConstraint
{
    public NullableColumnConstraint(string text, bool nullable) 
        : base(text)
    {
        Nullable = nullable;
    }

    public bool Nullable { get; }
}
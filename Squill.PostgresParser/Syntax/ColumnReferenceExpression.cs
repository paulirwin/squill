namespace Squill.PostgresParser.Syntax;

public class ColumnReferenceExpression : Expression
{
    public ColumnReferenceExpression(Identifier identifier)
    {
        Identifier = identifier;
    }

    public Identifier Identifier { get; }
}
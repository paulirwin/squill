namespace Squill.PostgresParser.Syntax;

public class PrimaryKeyColumnConstraint : ColumnConstraint
{
    public PrimaryKeyColumnConstraint(string text) 
        : base(text)
    {
    }
}
namespace Squill.Provider.Postgres.Syntax;

public class PrimaryKeyColumnConstraint : ColumnConstraint
{
    public PrimaryKeyColumnConstraint(string text) 
        : base(text)
    {
    }
}
namespace Squill.PostgresParser.Syntax;

public class NamedColumnConstraint : ColumnConstraint
{
    public NamedColumnConstraint(string text, string name, ColumnConstraint constraint) 
        : base(text)
    {
        Name = name;
        Constraint = constraint;
    }

    public string Name { get; }
    
    public ColumnConstraint Constraint { get; }
}
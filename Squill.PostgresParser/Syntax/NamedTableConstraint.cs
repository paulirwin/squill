namespace Squill.PostgresParser.Syntax;

public class NamedTableConstraint : TableConstraint
{
    public NamedTableConstraint(Identifier name, TableConstraint constraint)
    {
        Name = name;
        Constraint = constraint;
    }

    public Identifier Name { get; }
    
    public TableConstraint Constraint { get; }
}
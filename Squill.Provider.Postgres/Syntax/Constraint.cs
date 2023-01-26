namespace Squill.Provider.Postgres.Syntax;

public abstract class Constraint
{
    protected Constraint(string? name = null)
    {
        Name = name;
    }
    
    public string? Name { get; }
}
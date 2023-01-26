namespace Squill.Provider.Postgres.Syntax;

public class CreateTableStatement : Statement
{
    public CreateTableStatement(QualifiedName name)
    {
        Name = name;
    }

    public QualifiedName Name { get; }

    public IList<ITableElement> Elements { get; } = new List<ITableElement>();
}
namespace Squill.PostgresParser.Syntax;

public class CreateIndexStatement : Statement
{
    public CreateIndexStatement(Identifier? name,
        RelationExpression onRelation,
        bool unique,
        bool concurrently,
        bool ifNotExists,
        Identifier? usingMethod)
    {
        Name = name;
        Unique = unique;
        Concurrently = concurrently;
        IfNotExists = ifNotExists;
        OnRelation = onRelation;
        UsingMethod = usingMethod;
    }

    public Identifier? Name { get; }

    public bool Unique { get; }

    public bool Concurrently { get; }

    public bool IfNotExists { get; }

    public RelationExpression OnRelation { get; }

    public Identifier? UsingMethod { get; }

    public IList<IndexElement> Elements { get; } = new List<IndexElement>();
}
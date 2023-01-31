namespace Squill.PostgresParser.Syntax;

public class RelationExpression : SyntaxNode
{
    public RelationExpression(QualifiedName name, bool star, bool only)
    {
        Name = name;
        Star = star;
        Only = only;
    }

    public QualifiedName Name { get; }

    public bool Star { get; }

    public bool Only { get; }
}
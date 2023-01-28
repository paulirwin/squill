namespace Squill.PostgresParser.Syntax;

public abstract class Identifier : SyntaxNode
{
    public abstract string Name { get; }

    public override string ToString() => Name;
}
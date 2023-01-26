namespace Squill.Provider.Postgres.Syntax;

public class Root : SyntaxNode
{
    public IList<Statement> Statements { get; } = new List<Statement>();
}
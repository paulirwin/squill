namespace Squill.PostgresParser.Syntax;

public class Root : SyntaxNode
{
    public IList<Statement> Statements { get; } = new List<Statement>();
}
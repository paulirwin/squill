namespace Squill.PostgresParser.Syntax;

public class SyntaxList<T> : SyntaxNode
{
    public SyntaxList(IList<T> items)
    {
        Items = items;
    }
    
    public IList<T> Items { get; }
}
namespace Squill.Provider.Postgres.Syntax;

public class QualifiedName : SyntaxNode
{
    private readonly List<string> _segments = new();
    
    public QualifiedName(IEnumerable<string> segments)
    {
        _segments.AddRange(segments);
    }

    public QualifiedName(string input)
    {
        _segments.AddRange(input.Split('.'));
    }

    public IReadOnlyList<string> Segments => _segments;

    public override string ToString() => string.Join('.', _segments);
}
namespace Squill.PostgresParser.Syntax;

public class QualifiedName : SyntaxNode
{
    private readonly List<Identifier> _segments = new();
    
    public QualifiedName(IEnumerable<Identifier> segments)
    {
        _segments.AddRange(segments);
    }

    public IReadOnlyList<Identifier> Segments => _segments;

    public override string ToString() => string.Join('.', _segments);
}
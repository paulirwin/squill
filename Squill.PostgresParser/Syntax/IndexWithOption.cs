namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A single name/value pair from a CREATE INDEX ... WITH (...) clause, e.g. the
/// <c>m = 16</c> in an HNSW index. The value is captured as text since storage
/// parameters are free-form per access method.
/// </summary>
public class IndexWithOption : SyntaxNode
{
    public IndexWithOption(string name, string? value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }

    public string? Value { get; }
}

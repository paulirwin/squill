namespace Squill.PostgresParser.Syntax;

public abstract class SyntaxNode
{
    /// <summary>The 1-based line in the source text where this node starts, or null when not recorded.</summary>
    public int? Line { get; set; }

    /// <summary>The 1-based column in the source text where this node starts, or null when not recorded.</summary>
    public int? Column { get; set; }
}
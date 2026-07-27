namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>SUBSTRING</c> written in one of its keyword-separated forms (issue #140):
/// <c>SUBSTRING(s FROM start FOR count)</c>, either half alone, or the regex form
/// <c>SUBSTRING(s SIMILAR pattern ESCAPE escape)</c>.
///
/// The plain comma form <c>SUBSTRING(s, start, count)</c> is an ordinary call and parses to a
/// <see cref="FunctionApplicationExpression"/> instead.
/// </summary>
public class SubstringExpression : Expression
{
    public SubstringExpression(Expression source)
    {
        Source = source;
    }

    public Expression Source { get; }

    /// <summary>The <c>FROM</c> start position, or <c>null</c> when not written.</summary>
    public Expression? From { get; set; }

    /// <summary>The <c>FOR</c> length, or <c>null</c> when not written.</summary>
    public Expression? For { get; set; }

    /// <summary>The <c>SIMILAR</c> pattern of the regex form, or <c>null</c>.</summary>
    public Expression? Similar { get; set; }

    /// <summary>The <c>ESCAPE</c> character of the regex form, or <c>null</c>.</summary>
    public Expression? Escape { get; set; }
}

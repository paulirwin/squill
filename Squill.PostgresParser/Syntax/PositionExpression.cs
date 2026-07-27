namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>POSITION(substring IN source)</c> (issue #140). The operands are separated by the
/// <c>IN</c> keyword rather than a comma, and — unlike the underlying <c>strpos</c> — the
/// substring comes first.
/// </summary>
public class PositionExpression : Expression
{
    public PositionExpression(Expression substring, Expression source)
    {
        Substring = substring;
        Source = source;
    }

    public Expression Substring { get; }

    public Expression Source { get; }
}

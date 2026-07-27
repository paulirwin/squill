namespace Squill.PostgresParser.Syntax;

/// <summary>
/// An <c>OVERLAY(source PLACING replacement FROM start [FOR length])</c> (issue #140) —
/// substitutes <see cref="Replacement"/> into <see cref="Source"/> at <see cref="From"/>.
/// </summary>
public class OverlayExpression : Expression
{
    public OverlayExpression(Expression source, Expression replacement, Expression from,
        Expression? forLength = null)
    {
        Source = source;
        Replacement = replacement;
        From = from;
        For = forLength;
    }

    public Expression Source { get; }

    public Expression Replacement { get; }

    public Expression From { get; }

    /// <summary>The <c>FOR</c> length, or <c>null</c> when not written.</summary>
    public Expression? For { get; }
}

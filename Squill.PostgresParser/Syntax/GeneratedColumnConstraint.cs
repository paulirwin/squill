namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A generated (computed) column: <c>GENERATED ALWAYS AS (expr) STORED</c> (issue #120).
/// The column's value is derived from the expression rather than written directly.
///
/// Not to be confused with <see cref="IdentityColumnConstraint"/>: PostgreSQL spells both
/// with the <c>GENERATED</c> keyword, but an identity column draws from a sequence while a
/// generated column computes from other columns of the same row. PostgreSQL supports only
/// <c>STORED</c> generated columns (a virtual one is a syntax error), so no storage kind is
/// recorded here.
/// </summary>
public class GeneratedColumnConstraint : ColumnConstraint
{
    public GeneratedColumnConstraint(string text, Expression expression)
        : base(text)
    {
        Expression = expression;
    }

    /// <summary>
    /// The generation expression, as parsed from between the parentheses.
    /// </summary>
    public Expression Expression { get; }
}

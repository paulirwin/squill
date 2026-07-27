namespace Squill.PostgresParser.Syntax;

/// <summary>
/// Field selection or subscripting applied to a parenthesized expression — <c>(c).x</c>,
/// <c>(c).*</c>, <c>(a)[1]</c>, <c>(a)[1:2]</c> (issue #141).
///
/// The parentheses are required by PostgreSQL here rather than optional grouping: <c>c.x</c>
/// would read <c>x</c> as a column of table <c>c</c>, so <c>(c).x</c> is how a composite
/// column's field is selected. They are therefore part of this node rather than a nested
/// <see cref="ParenthesizedExpression"/>.
///
/// Each element is carried as written; Squill only needs to reproduce the accessor, not
/// interpret it.
/// </summary>
public class IndirectionExpression : Expression
{
    public IndirectionExpression(Expression expression, IList<string> elements)
    {
        Expression = expression;
        Elements = elements;
    }

    public Expression Expression { get; }

    /// <summary>
    /// The accessors applied in order, each including its punctuation — e.g. <c>.x</c> or
    /// <c>[1:2]</c>.
    /// </summary>
    public IList<string> Elements { get; }
}

namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>CAST(expr AS type)</c> — the SQL-standard spelling of a cast, as opposed to the
/// Postgres <c>expr::type</c> shorthand carried by <see cref="TypecastExpression"/>. The two
/// are kept distinct so an expression renders back out with the spelling it was written with.
///
/// <c>TREAT(expr AS type)</c> shares this node with <see cref="IsTreat"/> set; it has the same
/// shape and differs only in the keyword.
/// </summary>
public class CastExpression : Expression
{
    public CastExpression(Expression expression, DataType dataType, bool isTreat = false)
    {
        Expression = expression;
        DataType = dataType;
        IsTreat = isTreat;
    }

    public Expression Expression { get; }

    public DataType DataType { get; }

    /// <summary>True when written as <c>TREAT</c> rather than <c>CAST</c>.</summary>
    public bool IsTreat { get; }
}

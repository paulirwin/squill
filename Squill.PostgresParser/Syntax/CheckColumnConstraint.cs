namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A column- or domain-level <c>CHECK (expr)</c> constraint. The counterpart to
/// <see cref="CheckTableConstraint"/> for constraints written inline on a column definition
/// or in a <c>CREATE DOMAIN</c>'s constraint list, where the constrained value is referred
/// to by the keyword <c>VALUE</c> (in a domain) or is the column itself.
/// </summary>
public class CheckColumnConstraint : ColumnConstraint
{
    public CheckColumnConstraint(string text, Expression expression)
        : base(text)
    {
        Expression = expression;
    }

    public Expression Expression { get; }
}

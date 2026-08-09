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

    /// <summary>
    /// Whether the constraint carries <c>NO INHERIT</c> (issue #205). Inline this is the
    /// <c>no_inherit_</c> rule on the CHECK alternative itself (grammar :724), the counterpart
    /// to <see cref="CheckTableConstraint.IsNoInherit"/>.
    /// </summary>
    public bool IsNoInherit { get; set; }
}

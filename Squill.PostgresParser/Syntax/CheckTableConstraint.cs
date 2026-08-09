namespace Squill.PostgresParser.Syntax;

public class CheckTableConstraint : TableConstraint
{
    public CheckTableConstraint(Expression expression)
    {
        Expression = expression;
    }

    public Expression Expression { get; }

    /// <summary>
    /// Whether the constraint carries <c>NO INHERIT</c> (issue #205), which stops a child table
    /// from inheriting it. Measured against PostgreSQL 18, this round-trips as
    /// <c>pg_constraint.connoinherit</c>.
    /// </summary>
    ///
    /// <remarks>
    /// At table level the clause arrives as a <c>constraintattributeElem</c> (grammar :1367)
    /// rather than as the <c>no_inherit_</c> rule the inline spelling uses, because the
    /// table-level CHECK alternative has no <c>no_inherit_</c> of its own. Both routes set this
    /// same facet, so the two spellings produce one model.
    /// </remarks>
    public bool IsNoInherit { get; set; }
}
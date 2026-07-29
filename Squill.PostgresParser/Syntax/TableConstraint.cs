namespace Squill.PostgresParser.Syntax;

public abstract class TableConstraint : SyntaxNode, ITableElement
{
    /// <summary>
    /// Whether the constraint is <c>DEFERRABLE</c> (issue #160). False is the PostgreSQL
    /// default, and the spelling <c>NOT DEFERRABLE</c> means the same thing.
    ///
    /// Lives on the base rather than on each constraint because every <c>constraintelem</c>
    /// alternative ends in the same <c>constraintattributespec</c>, so a CHECK, UNIQUE,
    /// PRIMARY KEY and FOREIGN KEY all accept the clause.
    ///
    /// Unlike the inline spelling — where each attribute is a separate <c>colconstraint</c>
    /// sibling, so <see cref="ConstraintAttributeColumnConstraint"/> carries one facet and
    /// leaves the other null — a table-level spec is a single node holding the whole list.
    /// Both facets are therefore already resolved here, with the implication
    /// <c>INITIALLY DEFERRED ⇒ DEFERRABLE</c> applied by the visitor.
    /// </summary>
    public bool IsDeferrable { get; set; }

    /// <summary>
    /// Whether the constraint is <c>INITIALLY DEFERRED</c> (issue #160). False is the
    /// PostgreSQL default, and the spelling <c>INITIALLY IMMEDIATE</c> means the same thing.
    /// When true, <see cref="IsDeferrable"/> is necessarily true as well.
    /// </summary>
    public bool IsInitiallyDeferred { get; set; }
}

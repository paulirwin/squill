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

    /// <summary>
    /// Whether the constraint carries <c>NOT VALID</c> (issue #205) — the author's request that
    /// existing rows not be checked when the constraint is added.
    /// </summary>
    ///
    /// <remarks>
    /// Carried here but not modeled, because it does not round-trip from a CREATE TABLE.
    /// Measured against PostgreSQL 18: <c>ALTER TABLE ... ADD CONSTRAINT ... NOT VALID</c>
    /// stores <c>convalidated = f</c>, but the same clause inside <c>CREATE TABLE</c> is
    /// accepted and ignored, coming back <c>convalidated = t</c> — the table is new, so there
    /// are no existing rows to skip. Since Squill scripts a constraint inline or as a
    /// standalone ADD CONSTRAINT depending on dependency order, modeling this would make the
    /// facet round-trip on one path and re-diff forever on the other. The provider reports
    /// SQ1002 instead, which is the same treatment any other unmodelable construct gets.
    ///
    /// It is read rather than ignored so that the NOT of <c>NOT VALID</c> can never be
    /// mistaken for the NOT of <c>NOT DEFERRABLE</c>, and so the provider has something
    /// concrete to warn about.
    /// </remarks>
    public bool IsNotValid { get; set; }
}

namespace Squill.PostgresParser.Syntax;

/// <summary>
/// An inline constraint attribute (issue #159): <c>DEFERRABLE</c>, <c>NOT DEFERRABLE</c>,
/// <c>INITIALLY DEFERRED</c> or <c>INITIALLY IMMEDIATE</c>, written after a column-level
/// constraint such as a <c>REFERENCES</c> clause.
///
/// The grammar's <c>constraintattr</c> is a <c>colconstraint</c> alternative of its own, and
/// each keyword group is a separate alternative — so
/// <c>DEFERRABLE INITIALLY DEFERRED</c> arrives as two nodes, not one. Each node therefore
/// carries only the facet it states, leaving the other null: exactly one of
/// <see cref="Deferrable"/> and <see cref="InitiallyDeferred"/> is non-null.
/// </summary>
public class ConstraintAttributeColumnConstraint : ColumnConstraint
{
    public ConstraintAttributeColumnConstraint(string text, bool? deferrable, bool? initiallyDeferred)
        : base(text)
    {
        Deferrable = deferrable;
        InitiallyDeferred = initiallyDeferred;
    }

    /// <summary>
    /// True for <c>DEFERRABLE</c>, false for <c>NOT DEFERRABLE</c>, null when this node states
    /// an <c>INITIALLY</c> clause instead.
    /// </summary>
    public bool? Deferrable { get; }

    /// <summary>
    /// True for <c>INITIALLY DEFERRED</c>, false for <c>INITIALLY IMMEDIATE</c>, null when this
    /// node states a <c>DEFERRABLE</c> clause instead.
    /// </summary>
    public bool? InitiallyDeferred { get; }
}

namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A column-level <c>COLLATE "collation"</c> (issue #159), which fixes the collation used for
/// comparisons and sorting of the column's values.
///
/// A <c>colconstraint</c> alternative of its own rather than a <c>colconstraintelem</c>, which
/// is why it arrives here and not through <see cref="PostgresVisitor.VisitColconstraintelem"/>.
/// Distinct from <see cref="CollateExpression"/>, which applies a collation to an expression
/// rather than declaring one on a column.
/// </summary>
public class CollateColumnConstraint : ColumnConstraint
{
    public CollateColumnConstraint(string text, QualifiedName collation)
        : base(text)
    {
        Collation = collation;
    }

    /// <summary>
    /// The declared collation name. A collation name is case-sensitive and conventionally
    /// written quoted (<c>"POSIX"</c>), so the quotes are stripped but the case is kept.
    /// </summary>
    public QualifiedName Collation { get; }
}

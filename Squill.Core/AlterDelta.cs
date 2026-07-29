namespace Squill.Core;

/// <summary>
/// The kind of change applied to a single column when altering a table in place.
/// </summary>
public enum ColumnChangeKind
{
    /// <summary>A column present in the source model but not the target — ADD COLUMN.</summary>
    Add,

    /// <summary>A column present in the target model but not the source — DROP COLUMN.</summary>
    Drop,

    /// <summary>
    /// A column present in both, whose definition (type, nullability, …) differs —
    /// ALTER COLUMN.
    /// </summary>
    Alter,
}

/// <summary>
/// A single column-level change within an <see cref="AlterDelta"/>. For
/// <see cref="ColumnChangeKind.Add"/> and <see cref="ColumnChangeKind.Alter"/>,
/// <see cref="SourceColumn"/> is the desired column definition from the source model.
/// For <see cref="ColumnChangeKind.Drop"/>, <see cref="SourceColumn"/> is null and
/// <see cref="ColumnName"/> names the column to drop.
/// </summary>
public class ColumnChange
{
    public ColumnChange(
        ColumnChangeKind kind, string columnName, Element? sourceColumn, Element? targetColumn = null)
    {
        Kind = kind;
        ColumnName = columnName;
        SourceColumn = sourceColumn;
        TargetColumn = targetColumn;
    }

    public ColumnChangeKind Kind { get; }

    /// <summary>The canonical (table-qualified) name of the column being changed.</summary>
    public string ColumnName { get; }

    /// <summary>
    /// The desired column element from the source model, for Add/Alter. Null for Drop.
    /// </summary>
    public Element? SourceColumn { get; }

    /// <summary>
    /// The current column element from the target model, for Alter — so the generator can
    /// emit only the facets (type, nullability) that actually changed — and for Drop, where it
    /// is the column being dropped, so the generator can order the DROPs by the dependencies
    /// between them (a generated column must go before the columns its expression reads —
    /// issue #158). Null for Add.
    /// </summary>
    public Element? TargetColumn { get; }
}

/// <summary>
/// An in-place alteration of an existing top-level element (currently a table): the
/// element exists in both the source and target models but its definition differs, and
/// the difference can be expressed with ALTER statements rather than a full rebuild.
/// </summary>
public class AlterDelta : SchemaDelta
{
    public AlterDelta(Element sourceElement, Element targetElement)
    {
        SourceElement = sourceElement;
        TargetElement = targetElement;
    }

    /// <summary>The desired-state element from the source model (the DACPAC).</summary>
    public Element SourceElement { get; }

    /// <summary>The current-state element from the target model (the database).</summary>
    public Element TargetElement { get; }

    /// <summary>The ordered set of column-level changes to apply.</summary>
    public IList<ColumnChange> ColumnChanges { get; } = new List<ColumnChange>();
}

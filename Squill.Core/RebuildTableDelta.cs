namespace Squill.Core;

/// <summary>
/// A table alteration that cannot be expressed with in-place ALTER statements and so
/// requires rebuilding the table: create a new table with the desired shape, copy the
/// data over, drop the old table, and rename the new one into place. This is the
/// fallback when <see cref="AlterDelta"/> can't express the change — for example when a
/// column is inserted between existing columns and the physical column order must change
/// (issue #32).
/// </summary>
/// <remarks>
/// Rebuilding can be costly on large tables, so it is gated by an "allow table rebuild"
/// option (allowed by default, mirroring SSDT). When rebuild is disallowed and a change
/// requires it, <see cref="TableRebuildNotAllowedException"/> is thrown instead.
/// </remarks>
public class RebuildTableDelta : SchemaDelta
{
    public RebuildTableDelta(
        Element sourceElement, Element targetElement, string reason, bool dropsData)
    {
        SourceElement = sourceElement;
        TargetElement = targetElement;
        Reason = reason;
        DropsData = dropsData;
    }

    /// <summary>The desired-state table element from the source model (the DACPAC).</summary>
    public Element SourceElement { get; }

    /// <summary>The current-state table element from the target model (the database).</summary>
    public Element TargetElement { get; }

    /// <summary>
    /// A human-readable explanation of why a rebuild is required rather than an in-place
    /// ALTER (e.g. "a column was inserted between existing columns").
    /// </summary>
    public string Reason { get; }

    /// <summary>
    /// Whether this rebuild actually destroys data — true when the rebuild also drops a
    /// column that held data. A rebuild driven purely by reordering (e.g. inserting a new
    /// column mid-table) copies every existing row losslessly and does <em>not</em> set
    /// this, so it is not blocked by the data-loss guard. (Rebuilding is still "data
    /// motion", but SSDT's block-on-possible-data-loss is about loss, not movement.)
    /// </summary>
    public bool DropsData { get; }

    /// <summary>
    /// The table's dependent elements (PK, indexes, foreign keys) from the source model,
    /// so the rebuilt table can be recreated with its full shape — mirroring
    /// <see cref="CreateDelta.DependentElements"/>.
    /// </summary>
    public IList<Element> DependentElements { get; } = new List<Element>();

    /// <summary>
    /// The table's dependent elements (PK, indexes, foreign keys) as they exist in the
    /// target database. A rebuild renames the current table aside, so these must be
    /// renamed out of the way by their <em>actual database</em> names — which can differ
    /// from the source model's names — before the recreated table can reuse them.
    /// </summary>
    public IList<Element> TargetDependentElements { get; } = new List<Element>();
}

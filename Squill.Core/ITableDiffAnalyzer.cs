namespace Squill.Core;

/// <summary>
/// Compares a table element that exists in both the source (desired) and target
/// (current) models and decides how to reconcile them: an in-place set of column
/// changes (<see cref="AlterDelta"/>), a full table rebuild
/// (<see cref="RebuildTableDelta"/>) when the change can't be expressed with ALTER, or
/// no change at all. The column shape of a table is provider-specific (relationship and
/// element-type names differ per database), so this lives behind the provider rather
/// than in <see cref="SchemaCompare"/>.
/// </summary>
public interface ITableDiffAnalyzer
{
    /// <summary>
    /// Produces the delta needed to bring <paramref name="targetTable"/> (the current
    /// database state) into line with <paramref name="sourceTable"/> (the desired state
    /// from the DACPAC).
    /// </summary>
    /// <param name="sourceTable">The desired-state table element.</param>
    /// <param name="targetTable">The current-state table element.</param>
    /// <param name="sourceModel">
    /// The full source model, so dependent elements (PK, indexes, FKs) can be gathered for
    /// a rebuild.
    /// </param>
    /// <param name="targetModel">
    /// The full target (current database) model, so a rebuild can rename the current
    /// table's dependents aside by their actual database names, and detect inbound foreign
    /// keys from other tables.
    /// </param>
    /// <param name="allowTableRebuild">
    /// When <c>false</c>, a change that requires a rebuild throws
    /// <see cref="TableRebuildNotAllowedException"/> instead of producing a
    /// <see cref="RebuildTableDelta"/>.
    /// </param>
    /// <returns>
    /// An <see cref="AlterDelta"/> or <see cref="RebuildTableDelta"/> describing the
    /// change, or <c>null</c> when the two tables are already equivalent.
    /// </returns>
    SchemaDelta? DiffTable(
        Element sourceTable,
        Element targetTable,
        Model sourceModel,
        Model targetModel,
        bool allowTableRebuild);
}

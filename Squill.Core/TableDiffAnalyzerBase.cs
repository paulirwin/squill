namespace Squill.Core;

/// <summary>
/// The provider-agnostic core of table diffing: decides between an in-place
/// <see cref="AlterDelta"/> of ADD / DROP / ALTER COLUMN operations and a full
/// <see cref="RebuildTableDelta"/>, gathers a rebuild's dependent elements and inbound
/// foreign keys, and enforces <c>allowTableRebuild</c>. The physical-order rebuild rule
/// (a column reordered or inserted mid-table) is shared across engines.
///
/// The two provider-specific seams are the <see cref="DependencyAnalyzer"/> used to gather
/// a rebuilt table's dependents, and <see cref="ColumnChangeRequiresRebuild"/> — the check
/// for a column-definition change that cannot be expressed by the ALTER path and forces a
/// rebuild (Postgres identity options, MariaDB auto-increment).
/// </summary>
public abstract class TableDiffAnalyzerBase : ITableDiffAnalyzer
{
    /// <summary>The dependency analyzer used to gather a rebuilt table's dependent elements.</summary>
    protected abstract IDatabaseDependencyAnalyzer DependencyAnalyzer { get; }

    /// <summary>
    /// Whether an altered column's change cannot be applied in place and forces a table
    /// rebuild — e.g. an identity change (Postgres) or an auto-increment change (MariaDB).
    /// When it returns <c>true</c>, <paramref name="reason"/> describes the change for the
    /// rebuild delta and any <see cref="TableRebuildNotAllowedException"/>. Called only for
    /// columns present on both sides whose definitions already differ.
    /// </summary>
    protected abstract bool ColumnChangeRequiresRebuild(Element source, Element target, out string reason);

    public SchemaDelta? DiffTable(
        Element sourceTable,
        Element targetTable,
        Model sourceModel,
        Model targetModel,
        bool allowTableRebuild)
    {
        var tableName = sourceTable.Name
            ?? throw new ArgumentException("Tables must have names", nameof(sourceTable));

        var sourceColumns = GetOrderedColumns(sourceTable);
        var targetColumns = GetOrderedColumns(targetTable);

        // A change needs a rebuild when it can't be reproduced by appending/dropping at the
        // physical level: either a column common to both tables changed relative order, or a
        // new column is not at the tail (a new column can only be appended, so inserting one
        // mid-table changes the column order). This is issue #32's canonical example.
        if (RequiresRebuild(sourceColumns, targetColumns))
        {
            return BuildRebuild(sourceTable, targetTable, sourceModel, targetModel,
                allowTableRebuild, tableName,
                "a column was inserted or reordered among existing columns");
        }

        var changes = new List<ColumnChange>();

        // Dropped columns: present in the target (database) but not the source (DACPAC).
        foreach (var (name, _) in targetColumns)
        {
            if (!sourceColumns.Any(c => c.Name == name))
            {
                changes.Add(new ColumnChange(ColumnChangeKind.Drop, name, sourceColumn: null));
            }
        }

        // Added and altered columns, in source order.
        foreach (var (name, column) in sourceColumns)
        {
            var targetColumn = targetColumns.FirstOrDefault(c => c.Name == name).Column;

            if (targetColumn == null)
            {
                changes.Add(new ColumnChange(ColumnChangeKind.Add, name, column));
                continue;
            }

            // Both sides have the column; if its definition differs, alter it.
            if (!HashUtility.HashesEqual(column.Hash, targetColumn.Hash))
            {
                // A change the ALTER path's TYPE + nullability clauses can't express (identity
                // on Postgres, auto-increment on MariaDB) is applied by a rebuild instead of
                // being silently dropped.
                if (ColumnChangeRequiresRebuild(column, targetColumn, out var reason))
                {
                    return BuildRebuild(sourceTable, targetTable, sourceModel, targetModel,
                        allowTableRebuild, tableName,
                        $"column '{UnqualifiedOf(name)}' {reason}");
                }

                changes.Add(new ColumnChange(ColumnChangeKind.Alter, name, column, targetColumn));
            }
        }

        if (changes.Count == 0)
        {
            // The table element's hash differs but its columns are identical. A table element
            // hashes only its name, schema, and columns; a genuine column change would have
            // surfaced above, and a dependent object (index, PK, FK) is a separate element
            // whose change does not alter the table's hash — so this branch is not reachable
            // through normal diffing. Rather than silently perform a full data-moving rebuild
            // for a change we can't identify, fail loudly with context. (Standalone index
            // changes are handled by RecreateDelta in SchemaCompare, not here.)
            throw new InvalidOperationException(
                $"Table '{UnqualifiedOf(tableName)}' differs from the target but no "
                + "column change was detected; refusing a blind rebuild. This likely "
                + "indicates a change Squill does not yet model.");
        }

        var alterDelta = new AlterDelta(sourceTable, targetTable);

        foreach (var change in changes)
        {
            alterDelta.ColumnChanges.Add(change);
        }

        return alterDelta;
    }

    // Whether the change can only be applied by rebuilding the table. A column can be dropped
    // or appended at the end, but not reordered or inserted in the middle. So a rebuild is
    // required when either:
    //   1. the columns common to both tables appear in a different relative order, or
    //   2. a new column (present only in the source) is followed by any existing column —
    //      i.e. it isn't at the tail — since ADD COLUMN would place it at the end instead.
    private static bool RequiresRebuild(
        IList<(string Name, Element Column)> source,
        IList<(string Name, Element Column)> target)
    {
        var targetNames = target.Select(c => c.Name).ToHashSet();
        var sourceNames = source.Select(c => c.Name).ToHashSet();

        // 1. The subsequence of columns present in both, taken in each table's own order,
        // must match — otherwise a common column would have to move.
        var commonInSourceOrder = source.Where(c => targetNames.Contains(c.Name))
            .Select(c => c.Name).ToList();
        var commonInTargetOrder = target.Where(c => sourceNames.Contains(c.Name))
            .Select(c => c.Name).ToList();

        if (!commonInSourceOrder.SequenceEqual(commonInTargetOrder))
        {
            return true;
        }

        // 2. Every new column must come after all existing columns in the source order. Once
        // we've seen a brand-new column, encountering an existing column afterwards means the
        // new one was inserted mid-table and can't just be appended.
        var seenNewColumn = false;

        foreach (var (name, _) in source)
        {
            if (!targetNames.Contains(name))
            {
                seenNewColumn = true;
            }
            else if (seenNewColumn)
            {
                return true;
            }
        }

        return false;
    }

    private SchemaDelta BuildRebuild(
        Element sourceTable,
        Element targetTable,
        Model sourceModel,
        Model targetModel,
        bool allowTableRebuild,
        string tableName,
        string reason)
    {
        // A rebuild renames the current table aside and drops it, which fails while another
        // table's foreign key references it (the dependency survives the rename). Those
        // inbound FKs are dropped before the rebuild and recreated after, inside the rebuild
        // transaction.
        var inboundForeignKeys = GetInboundForeignKeys(tableName, targetModel);

        if (!allowTableRebuild)
        {
            throw new TableRebuildNotAllowedException(UnqualifiedOf(tableName), reason);
        }

        // The rebuild destroys data only if it drops a column the target still has — a rebuild
        // driven purely by reordering copies every row losslessly. This drives the data-loss
        // guard, so a lossless mid-table insert isn't blocked.
        var sourceColumnNames = GetOrderedColumns(sourceTable).Select(c => c.Name).ToHashSet();
        var dropsData = GetOrderedColumns(targetTable)
            .Any(c => !sourceColumnNames.Contains(c.Name));

        var delta = new RebuildTableDelta(sourceTable, targetTable, reason, dropsData);

        // Carry the desired dependent elements (PK, indexes, FKs) so the rebuilt table is
        // recreated with its full shape, mirroring CreateDelta.
        foreach (var dependent in DependencyAnalyzer.GetDependentElements(sourceTable, sourceModel)
                     ?? Enumerable.Empty<Element>())
        {
            delta.DependentElements.Add(dependent);
        }

        // Carry the current database's dependents so their actual names can be renamed aside
        // before the recreated table reuses them (the DB's PK/index names can differ from the
        // source model's).
        foreach (var dependent in DependencyAnalyzer.GetDependentElements(targetTable, targetModel)
                     ?? Enumerable.Empty<Element>())
        {
            delta.TargetDependentElements.Add(dependent);
        }

        // Carry the inbound FKs (from other tables) so the generator can drop them before the
        // rebuild and recreate them after.
        foreach (var inboundForeignKey in inboundForeignKeys)
        {
            delta.InboundForeignKeys.Add(inboundForeignKey);
        }

        return delta;
    }

    // Foreign keys defined on other tables that reference the named table. A self-referencing
    // FK is dropped with the table itself, so it doesn't need reconciling; only a reference
    // from a different table does.
    private static IList<Element> GetInboundForeignKeys(string tableName, Model model)
    {
        var inbound = new List<Element>();

        foreach (var element in model.Elements
                     .Where(i => i.Type == SqlElementTypes.SqlForeignKeyConstraint))
        {
            var foreignTable = element.GetRelationship(SqlRelationshipNames.ForeignTable)
                ?.Entries.OfType<Reference>().SingleOrDefault();

            var definingTable = element.GetRelationship(SqlRelationshipNames.DefiningTable)
                ?.Entries.OfType<Reference>().SingleOrDefault();

            if (foreignTable?.Name == tableName && definingTable?.Name != tableName)
            {
                inbound.Add(element);
            }
        }

        return inbound;
    }

    // The table's columns in declaration order, as (canonical name, element) pairs.
    private static IList<(string Name, Element Column)> GetOrderedColumns(Element table)
    {
        var columns = new List<(string, Element)>();

        foreach (var columnRelationship in table.Relationships
                     .Where(i => i.Name == SqlRelationshipNames.Columns))
        {
            foreach (var column in columnRelationship.Entries.OfType<Element>()
                         .Where(i => i.Type == SqlElementTypes.SqlSimpleColumn))
            {
                if (column.Name is string name)
                {
                    columns.Add((name, column));
                }
            }
        }

        return columns;
    }

    // The last segment of a canonical (unquoted, dot-joined) name. Quote-independent, so it
    // is computed here rather than through a provider's SqlName.
    private static string UnqualifiedOf(string canonical) => canonical.Split('.')[^1];
}

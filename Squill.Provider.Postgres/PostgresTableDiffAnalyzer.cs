using Squill.Core;

namespace Squill.Provider.Postgres;

/// <summary>
/// Diffs two PostgreSQL table elements (desired vs. current) and decides how to
/// reconcile them: an in-place <see cref="AlterDelta"/> of ADD / DROP / ALTER COLUMN
/// operations when the change can be expressed that way, or a
/// <see cref="RebuildTableDelta"/> when it cannot (e.g. a column inserted between
/// existing columns, which would change the physical column order).
/// </summary>
public class PostgresTableDiffAnalyzer : ITableDiffAnalyzer
{
    private static readonly PostgresDatabaseDependencyAnalyzer DependencyAnalyzer = new();

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

        // A change needs a rebuild when it can't be reproduced by appending/dropping at
        // the physical level: either a column common to both tables changed relative
        // order, or a new column is not at the tail (Postgres appends new columns
        // physically at the end, so inserting one mid-table changes the column order).
        // This is issue #32's canonical example.
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
                // An identity change (adding, removing, or switching ALWAYS/BY DEFAULT
                // GENERATED AS IDENTITY) can't be expressed by the ALTER path's TYPE +
                // nullability clauses; a rebuild recreates the column with the desired
                // identity instead of silently dropping the change.
                if (IdentityDiffers(column, targetColumn))
                {
                    return BuildRebuild(sourceTable, targetTable, sourceModel, targetModel,
                        allowTableRebuild, tableName,
                        $"column '{SqlName.UnqualifiedOf(name)}' changed its identity definition");
                }

                changes.Add(new ColumnChange(ColumnChangeKind.Alter, name, column, targetColumn));
            }
        }

        if (changes.Count == 0)
        {
            // The table element's hash differs but its columns are identical. A table
            // element hashes only its name, schema, and columns; a genuine column change
            // would have surfaced as a column diff above, and a dependent object (index,
            // PK, FK) is a separate element whose change does not alter the table's hash —
            // so this branch is not reachable through normal diffing. Rather than silently
            // perform a full data-moving rebuild for a change we can't identify, fail
            // loudly with context. (Standalone index changes are handled by RecreateDelta
            // in SchemaCompare, not here.)
            throw new InvalidOperationException(
                $"Table '{SqlName.UnqualifiedOf(tableName)}' differs from the target but no "
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

    // Whether the change can only be applied by rebuilding the table. Postgres can drop
    // any column and append a new column at the end, but it cannot reorder columns or
    // insert one in the middle. So a rebuild is required when either:
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

        // 2. Every new column must come after all existing columns in the source order.
        // Once we've seen a brand-new column, encountering an existing column afterwards
        // means the new one was inserted mid-table and can't just be appended.
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

    // Whether two versions of the same column differ in their identity definition.
    private static bool IdentityDiffers(Element source, Element target)
    {
        var sourceIdentity = source.GetProperty<bool?>(PostgresPropertyNames.IsIdentity) == true;
        var targetIdentity = target.GetProperty<bool?>(PostgresPropertyNames.IsIdentity) == true;

        if (sourceIdentity != targetIdentity)
        {
            return true;
        }

        return sourceIdentity
            && source.GetProperty<string>(PostgresPropertyNames.IdentityGeneration)
                != target.GetProperty<string>(PostgresPropertyNames.IdentityGeneration);
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
        // inbound FKs are dropped before the rebuild and recreated after, inside the
        // rebuild transaction.
        var inboundForeignKeys = GetInboundForeignKeys(tableName, targetModel);

        if (!allowTableRebuild)
        {
            throw new TableRebuildNotAllowedException(SqlName.UnqualifiedOf(tableName), reason);
        }

        // The rebuild destroys data only if it drops a column the target still has — a
        // rebuild driven purely by reordering copies every row losslessly. This drives the
        // data-loss guard, so a lossless mid-table insert isn't blocked.
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

        // Carry the current database's dependents so their actual names can be renamed
        // aside before the recreated table reuses them (the DB's PK/index names can differ
        // from the source model's).
        foreach (var dependent in DependencyAnalyzer.GetDependentElements(targetTable, targetModel)
                     ?? Enumerable.Empty<Element>())
        {
            delta.TargetDependentElements.Add(dependent);
        }

        // Carry the inbound FKs (from other tables) so the generator can drop them before
        // the rebuild and recreate them after.
        foreach (var inboundForeignKey in inboundForeignKeys)
        {
            delta.InboundForeignKeys.Add(inboundForeignKey);
        }

        return delta;
    }

    // Foreign keys defined on other tables that reference the named table. A self-
    // referencing FK is dropped with the table itself, so it doesn't need reconciling;
    // only a reference from a different table does.
    private static IList<Element> GetInboundForeignKeys(string tableName, Model model)
    {
        var inbound = new List<Element>();

        foreach (var element in model.Elements
                     .Where(i => i.Type == PostgresElementTypes.SqlForeignKeyConstraint))
        {
            var foreignTable = element.GetRelationship(PostgresRelationshipNames.ForeignTable)
                ?.Entries.OfType<Reference>().SingleOrDefault();

            var definingTable = element.GetRelationship(PostgresRelationshipNames.DefiningTable)
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
                     .Where(i => i.Name == PostgresRelationshipNames.Columns))
        {
            foreach (var column in columnRelationship.Entries.OfType<Element>()
                         .Where(i => i.Type == PostgresElementTypes.SqlSimpleColumn))
            {
                if (column.Name is string name)
                {
                    columns.Add((name, column));
                }
            }
        }

        return columns;
    }
}

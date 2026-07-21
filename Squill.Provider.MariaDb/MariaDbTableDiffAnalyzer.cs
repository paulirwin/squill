using Squill.Core;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Diffs two MariaDB table elements (desired vs. current) and decides how to reconcile
/// them: an in-place <see cref="AlterDelta"/> of ADD / DROP / ALTER COLUMN operations when
/// the change can be expressed that way, or a <see cref="RebuildTableDelta"/> when it
/// cannot (e.g. a column inserted between existing columns, or an auto-increment change).
/// </summary>
public class MariaDbTableDiffAnalyzer : ITableDiffAnalyzer
{
    private static readonly MariaDbDatabaseDependencyAnalyzer DependencyAnalyzer = new();

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

        // A change needs a rebuild when a column common to both tables changed relative
        // order, or a new column is inserted mid-table. (MariaDB's ALTER ... AFTER could
        // express these in place, but a rebuild is the safe, shared path and matches the
        // Postgres provider's behavior.)
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

            if (!HashUtility.HashesEqual(column.Hash, targetColumn.Hash))
            {
                // An auto-increment change can't be expressed by the ALTER path's type +
                // nullability clauses cleanly; a rebuild recreates the column with the
                // desired auto-increment instead of silently dropping the change.
                if (AutoIncrementDiffers(column, targetColumn))
                {
                    return BuildRebuild(sourceTable, targetTable, sourceModel, targetModel,
                        allowTableRebuild, tableName,
                        $"column '{SqlName.UnqualifiedOf(name)}' changed its auto-increment definition");
                }

                changes.Add(new ColumnChange(ColumnChangeKind.Alter, name, column, targetColumn));
            }
        }

        if (changes.Count == 0)
        {
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

    // Whether the change can only be applied by rebuilding the table: either the columns
    // common to both tables appear in a different relative order, or a new column is not at
    // the tail.
    private static bool RequiresRebuild(
        IList<(string Name, Element Column)> source,
        IList<(string Name, Element Column)> target)
    {
        var targetNames = target.Select(c => c.Name).ToHashSet();
        var sourceNames = source.Select(c => c.Name).ToHashSet();

        var commonInSourceOrder = source.Where(c => targetNames.Contains(c.Name))
            .Select(c => c.Name).ToList();
        var commonInTargetOrder = target.Where(c => sourceNames.Contains(c.Name))
            .Select(c => c.Name).ToList();

        if (!commonInSourceOrder.SequenceEqual(commonInTargetOrder))
        {
            return true;
        }

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

    private static bool AutoIncrementDiffers(Element source, Element target)
        => (source.GetProperty<bool?>(MariaDbPropertyNames.IsAutoIncrement) == true)
            != (target.GetProperty<bool?>(MariaDbPropertyNames.IsAutoIncrement) == true);

    private SchemaDelta BuildRebuild(
        Element sourceTable,
        Element targetTable,
        Model sourceModel,
        Model targetModel,
        bool allowTableRebuild,
        string tableName,
        string reason)
    {
        var inboundForeignKeys = GetInboundForeignKeys(tableName, targetModel);

        if (!allowTableRebuild)
        {
            throw new TableRebuildNotAllowedException(SqlName.UnqualifiedOf(tableName), reason);
        }

        var sourceColumnNames = GetOrderedColumns(sourceTable).Select(c => c.Name).ToHashSet();
        var dropsData = GetOrderedColumns(targetTable)
            .Any(c => !sourceColumnNames.Contains(c.Name));

        var delta = new RebuildTableDelta(sourceTable, targetTable, reason, dropsData);

        foreach (var dependent in DependencyAnalyzer.GetDependentElements(sourceTable, sourceModel)
                     ?? Enumerable.Empty<Element>())
        {
            delta.DependentElements.Add(dependent);
        }

        foreach (var dependent in DependencyAnalyzer.GetDependentElements(targetTable, targetModel)
                     ?? Enumerable.Empty<Element>())
        {
            delta.TargetDependentElements.Add(dependent);
        }

        foreach (var inboundForeignKey in inboundForeignKeys)
        {
            delta.InboundForeignKeys.Add(inboundForeignKey);
        }

        return delta;
    }

    private static IList<Element> GetInboundForeignKeys(string tableName, Model model)
    {
        var inbound = new List<Element>();

        foreach (var element in model.Elements
                     .Where(i => i.Type == MariaDbElementTypes.SqlForeignKeyConstraint))
        {
            var foreignTable = element.GetRelationship(MariaDbRelationshipNames.ForeignTable)
                ?.Entries.OfType<Reference>().SingleOrDefault();

            var definingTable = element.GetRelationship(MariaDbRelationshipNames.DefiningTable)
                ?.Entries.OfType<Reference>().SingleOrDefault();

            if (foreignTable?.Name == tableName && definingTable?.Name != tableName)
            {
                inbound.Add(element);
            }
        }

        return inbound;
    }

    private static IList<(string Name, Element Column)> GetOrderedColumns(Element table)
    {
        var columns = new List<(string, Element)>();

        foreach (var columnRelationship in table.Relationships
                     .Where(i => i.Name == MariaDbRelationshipNames.Columns))
        {
            foreach (var column in columnRelationship.Entries.OfType<Element>()
                         .Where(i => i.Type == MariaDbElementTypes.SqlSimpleColumn))
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

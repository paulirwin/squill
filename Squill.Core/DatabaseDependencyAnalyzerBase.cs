namespace Squill.Core;

/// <summary>
/// The provider-agnostic core of <see cref="IDatabaseDependencyAnalyzer"/>: the element-type
/// classification shared across engines (a PK / index / FK is a dependent of its table; a
/// table is the table type; an index is a droppable standalone dependent; dropping a table
/// loses data), plus the foreign-key walk that gathers a table's dependents and its create
/// dependencies.
///
/// Schema/extension features and the create-order and replaceable-type rankings are
/// engine-specific, so they are abstract or default to inert here. A provider with no schema
/// concept (MariaDB) inherits the base behavior as-is; a schema-scoped provider (Postgres)
/// overrides the disambiguation seams (<see cref="DependentBelongsToTable"/>,
/// <see cref="ForeignKeyBelongsToTable"/>, <see cref="ResolveReferencedTable"/>) and the
/// schema/extension members.
/// </summary>
public abstract class DatabaseDependencyAnalyzerBase : IDatabaseDependencyAnalyzer
{
    public bool IsDependentElementType(string type)
        => type is SqlElementTypes.SqlPrimaryKeyConstraint
            or SqlElementTypes.SqlUniqueConstraint
            or SqlElementTypes.SqlIndex
            or SqlElementTypes.SqlForeignKeyConstraint;

    public bool IsTableElementType(string type)
        => type == SqlElementTypes.SqlTable;

    public bool DropCausesDataLoss(string type)
        => type == SqlElementTypes.SqlTable;

    // An index or a unique constraint can be created and dropped on its own, so a change to
    // one on an otherwise-unchanged table is reconciled without touching the table. A PK or
    // FK is not: those are reconciled through their table.
    public bool IsDroppableStandaloneDependent(string type)
        => type is SqlElementTypes.SqlIndex or SqlElementTypes.SqlUniqueConstraint;

    // No extension concept by default (Postgres overrides).
    public virtual bool IsExtensionElementType(string type) => false;

    public virtual string? GetExtensionVersion(Element extension) => null;

    // Not schema-scoped by default (Postgres overrides).
    public virtual string? GetElementSchema(Element element) => null;

    // No extension version to normalize by default (Postgres overrides).
    public virtual Element NormalizeForComparison(Element source, Element target) => source;

    public abstract bool IsReplaceableElementType(string type);

    public abstract int GetCreateOrder(string type);

    public IEnumerable<CreateDependency> GetCreateDependencies(Element element, Model model)
    {
        // Only a table orders against other elements, and it does so through the foreign keys
        // defined on it — separate elements referencing it as their defining table.
        if (element.Type != SqlElementTypes.SqlTable || element.Name is not string tableName)
        {
            yield break;
        }

        foreach (var foreignKey in model.Elements.Where(i =>
                     i.Type == SqlElementTypes.SqlForeignKeyConstraint))
        {
            if (foreignKey.GetRelationship(SqlRelationshipNames.DefiningTable)
                    ?.GetReference(tableName) is null)
            {
                continue;
            }

            if (!ForeignKeyBelongsToTable(foreignKey, element, model))
            {
                continue;
            }

            var referenced = foreignKey.GetRelationship(SqlRelationshipNames.ForeignTable)
                ?.Entries.OfType<Reference>().FirstOrDefault();

            if (referenced is null)
            {
                continue;
            }

            if (ResolveReferencedTable(referenced.Name, model) is { } referencedTable)
            {
                yield return new CreateDependency(referencedTable, foreignKey);
            }
        }
    }

    public IList<Element>? GetDependentElements(Element sourceElement, Model model)
    {
        if (sourceElement.Type != SqlElementTypes.SqlTable
            || sourceElement.Name is not string tableName)
        {
            return null;
        }

        var deps = new List<Element>();

        foreach (var element in model.Elements.Where(i => IsDependentElementType(i.Type)))
        {
            var tableRelationshipName = element.Type == SqlElementTypes.SqlIndex
                ? SqlRelationshipNames.IndexedObject
                : SqlRelationshipNames.DefiningTable;

            var reference = element.GetRelationship(tableRelationshipName)?.GetReference(tableName);

            if (reference == null)
            {
                continue;
            }

            if (DependentBelongsToTable(element, sourceElement, model))
            {
                deps.Add(element);
            }
        }

        return deps;
    }

    /// <summary>
    /// Whether <paramref name="dependent"/> (a PK/index/FK whose table reference already
    /// matches <paramref name="table"/> by bare name) truly belongs to that table. The base
    /// answers <c>true</c> on the name match alone; a schema-scoped provider overrides to also
    /// require a schema match, so two same-named tables in different schemas don't pull in each
    /// other's dependents.
    /// </summary>
    protected virtual bool DependentBelongsToTable(Element dependent, Element table, Model model) => true;

    /// <summary>
    /// Whether <paramref name="foreignKey"/> (whose defining-table reference already matches
    /// <paramref name="table"/> by bare name) truly belongs to that table. As with
    /// <see cref="DependentBelongsToTable"/>, the base answers <c>true</c> on the name match;
    /// a schema-scoped provider overrides to require a schema match too.
    /// </summary>
    protected virtual bool ForeignKeyBelongsToTable(Element foreignKey, Element table, Model model) => true;

    /// <summary>
    /// Resolves a foreign key's referenced-table name to its table element. The base matches by
    /// bare name (MariaDB has no schemas within a database); a schema-scoped provider overrides
    /// to resolve a possibly schema-qualified name.
    /// </summary>
    protected virtual Element? ResolveReferencedTable(string referencedName, Model model)
        => model.Elements.FirstOrDefault(i =>
            i.Type == SqlElementTypes.SqlTable
            && string.Equals(i.Name, referencedName, StringComparison.Ordinal));
}

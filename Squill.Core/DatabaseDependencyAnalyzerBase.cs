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
            or SqlElementTypes.SqlCheckConstraint
            or SqlElementTypes.SqlIndex
            or SqlElementTypes.SqlForeignKeyConstraint;

    public bool IsTableElementType(string type)
        => type == SqlElementTypes.SqlTable;

    public bool DropCausesDataLoss(string type)
        => type == SqlElementTypes.SqlTable;

    // An index, a unique constraint or a CHECK constraint can be created and dropped on its
    // own, so a change to one on an otherwise-unchanged table is reconciled without touching
    // the table. A PK or FK is not: those are reconciled through their table.
    public bool IsDroppableStandaloneDependent(string type)
        => type is SqlElementTypes.SqlIndex
            or SqlElementTypes.SqlUniqueConstraint
            or SqlElementTypes.SqlCheckConstraint;

    // No extension concept by default (Postgres overrides).
    public virtual bool IsExtensionElementType(string type) => false;

    public virtual string? GetExtensionVersion(Element extension) => null;

    // Not schema-scoped by default (Postgres overrides).
    public virtual string? GetElementSchema(Element element) => null;

    // No extension version to normalize by default (Postgres overrides).
    public virtual Element NormalizeForComparison(Element source, Element target) => source;

    // No in-place alteration beyond the generic handling by default (Postgres overrides for
    // enums and domains, which cannot be dropped while a column uses them — issue #122).
    public virtual SchemaDelta? GetInPlaceAlterDelta(Element source, Element target) => null;

    // Almost every property is part of its element's identity; the exceptions are properties
    // carrying text the engine rewrites when it stores it, which could therefore never match
    // what a comparison reads back (issue #122). A view's query is one on every engine —
    // PostgreSQL and MariaDB both reformat it — so it is excluded here; a provider overrides to
    // add its own (Postgres: a domain's CHECK predicate).
    public virtual bool ParticipatesInIdentity(string elementType, string propertyName)
        => (elementType, propertyName) switch
        {
            (SqlElementTypes.SqlView, SqlPropertyNames.Definition) => false,
            // A CHECK predicate and a generated column's expression are rewritten by every
            // engine when stored (parentheses and casts added), so the RAW text of one could
            // never hash-match what is read back (issue #120). What participates in its place is
            // the canonical form carried alongside it — NormalizedCheckExpression /
            // NormalizedGeneratedExpression, which are not listed here and so participate by
            // default. That is what makes redefining a predicate under the same name a change
            // the deploy acts on rather than a silent no-op (issue #156).
            //
            // When an expression has no canonical form the normalized property is simply absent,
            // and the raw one being excluded here is what makes the element fall back to the
            // pre-#156 behaviour instead of re-diffing on every deploy.
            (SqlElementTypes.SqlCheckConstraint, SqlPropertyNames.CheckExpression) => false,
            (SqlElementTypes.SqlSimpleColumn, SqlPropertyNames.GeneratedExpression) => false,
            _ => true,
        };

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

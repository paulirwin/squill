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
    // Virtual so a provider can add the dependent types only its engine has: an EXCLUDE
    // constraint is a table dependent on Postgres (issue #212) and has no MariaDB equivalent,
    // so it cannot be named in this shared vocabulary.
    public virtual bool IsDependentElementType(string type)
        => type is SqlElementTypes.SqlPrimaryKeyConstraint
            or SqlElementTypes.SqlUniqueConstraint
            or SqlElementTypes.SqlCheckConstraint
            or SqlElementTypes.SqlIndex
            or SqlElementTypes.SqlForeignKeyConstraint;

    public bool IsTableElementType(string type)
        => type == SqlElementTypes.SqlTable;

    public bool DropCausesDataLoss(string type)
        => type == SqlElementTypes.SqlTable;

    // Every dependent Squill models can be created and dropped on its own, so a change to one
    // on an otherwise-unchanged table is reconciled without touching the table. That has to
    // include the primary and foreign keys: both are separate elements, so neither changes its
    // table's hash, and while they were excluded here nothing was left to notice a key being
    // added, moved or removed — the deploy reported success and changed nothing (issue #157).
    //
    // "Droppable standalone" is about the DDL existing, not about the change being free: a
    // constraint holds no data of its own, so reconciling one is never a data-loss operation,
    // but the engine still validates a newly added key against the existing rows and rejects it
    // if they violate it.
    public bool IsDroppableStandaloneDependent(string type)
        => IsDependentElementType(type);

    // No extension concept by default (Postgres overrides).
    public virtual bool IsExtensionElementType(string type) => false;

    public virtual string? GetExtensionVersion(Element extension) => null;

    // Not schema-scoped by default (Postgres overrides).
    public virtual string? GetElementSchema(Element element) => null;

    // Beyond the engine-specific normalization a provider adds, one rule is universal: a
    // canonical expression only counts when BOTH sides have one (issue #156).
    public virtual Element NormalizeForComparison(Element source, Element target)
        => DropOneSidedNormalizedExpressions(source, target);

    /// <summary>
    /// Removes a normalized-expression property from the source when the target has no
    /// counterpart (or vice versa), so an expression only one side could canonicalize does not
    /// read as a change.
    ///
    /// The normalizer refuses anything with no measured canonical form, and the two sides are
    /// spelled differently — the source as written, the target as the catalog reports it — so one
    /// can succeed where the other fails. <c>LIKE … ESCAPE</c> is the measured case: the declared
    /// form is refused, while the extracted form parses as a plain operator and normalizes fine.
    /// Left alone, the element would differ purely because one side carries an extra property,
    /// producing a delta on every deploy for an expression that never changed.
    ///
    /// Dropping the property falls back to the pre-#156 behaviour for that one expression: a
    /// redefinition of it is missed, rather than an unchanged one redeploying forever.
    ///
    /// Only the source can be rewritten here, so the rule is expressed as "keep a normalized
    /// property only when the target has the same one". That covers both directions: a property
    /// the source alone has is dropped outright, and one the target alone has cannot contribute
    /// to the source's hash anyway — but the target's extra property WOULD still differ, so the
    /// comparison also has to hash the target without it. <see cref="NormalizedForComparison"/>
    /// is what callers use to get that matched pair.
    /// </summary>
    protected static Element DropOneSidedNormalizedExpressions(Element source, Element target)
    {
        var unmatched = source.Properties
            .Where(property => IsNormalizedExpression(property.Name)
                && !target.Properties.Any(other => other.Name == property.Name))
            .ToList();

        if (unmatched.Count == 0)
        {
            return source;
        }

        var copy = new Element(source.Type) { Name = source.Name };

        foreach (var relationship in source.Relationships)
        {
            copy.Relationships.Add(relationship);
        }

        foreach (var property in source.Properties.Except(unmatched))
        {
            copy.Properties.Add(property);
        }

        foreach (var annotation in source.Annotations)
        {
            copy.Annotations.Add(annotation);
        }

        return copy;
    }

    private static bool IsNormalizedExpression(string propertyName)
        => propertyName is SqlPropertyNames.NormalizedCheckExpression
            or SqlPropertyNames.NormalizedGeneratedExpression;

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

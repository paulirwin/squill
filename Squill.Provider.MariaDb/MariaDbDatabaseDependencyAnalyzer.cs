using Squill.Core;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Encodes MariaDB element dependency rules for schema comparison: which element types are
/// dependent on a table (primary keys, indexes, foreign keys), which is the table type, and
/// how a table's dependents are gathered so they can be attached to its create/rebuild
/// delta. MariaDB has no schema or extension objects, so the schema/extension-specific
/// members are inert.
/// </summary>
public class MariaDbDatabaseDependencyAnalyzer : IDatabaseDependencyAnalyzer
{
    public bool IsDependentElementType(string type)
        => type is MariaDbElementTypes.SqlPrimaryKeyConstraint
            or MariaDbElementTypes.SqlIndex
            or MariaDbElementTypes.SqlForeignKeyConstraint;

    public bool IsTableElementType(string type)
        => type == MariaDbElementTypes.SqlTable;

    // MariaDB has no extension concept.
    public bool IsExtensionElementType(string type) => false;

    public string? GetExtensionVersion(Element extension) => null;

    // A procedure's definition is replaced wholesale rather than altered facet by facet. A
    // view is here too; both are scripted as DROP + CREATE, since CREATE OR REPLACE is
    // MariaDB-only syntax and this provider targets MySQL as well.
    public bool IsReplaceableElementType(string type)
        => type is MariaDbElementTypes.SqlProcedure or MariaDbElementTypes.SqlView;

    public bool DropCausesDataLoss(string type)
        => type == MariaDbElementTypes.SqlTable;

    public bool IsDroppableStandaloneDependent(string type)
        => type == MariaDbElementTypes.SqlIndex;

    // MariaDB objects are not schema-scoped within a database (the database is the schema),
    // so no element carries a schema for identity.
    public string? GetElementSchema(Element element) => null;

    // MariaDB has no schema/extension objects that must precede tables, so every element
    // sorts to the same create order — except a view and a procedure, which reference
    // tables. Neither is parsed for dependencies beyond its source tables, so this ordering
    // is what makes a view or procedure that reads a table in the same deploy work.
    // A view selects from tables, so it follows them; a procedure body may query either, so
    // it comes last.
    public int GetCreateOrder(string type) => type switch
    {
        MariaDbElementTypes.SqlView => 1,
        MariaDbElementTypes.SqlProcedure => 2,
        _ => 0,
    };

    // MariaDB has no extension version to normalize, so the source is compared as-is.
    public IEnumerable<CreateDependency> GetCreateDependencies(Element element, Model model)
    {
        // A table must follow the tables its foreign keys reference. MariaDB has no
        // schemas within a database, so a referenced table resolves by bare name.
        if (element.Type != MariaDbElementTypes.SqlTable || element.Name is not string tableName)
        {
            yield break;
        }

        foreach (var foreignKey in model.Elements.Where(i =>
                     i.Type == MariaDbElementTypes.SqlForeignKeyConstraint))
        {
            if (foreignKey.GetRelationship(MariaDbRelationshipNames.DefiningTable)
                    ?.GetReference(tableName) is null)
            {
                continue;
            }

            var referenced = foreignKey.GetRelationship(MariaDbRelationshipNames.ForeignTable)
                ?.Entries.OfType<Reference>().FirstOrDefault();

            if (referenced is null)
            {
                continue;
            }

            var referencedTable = model.Elements.FirstOrDefault(i =>
                i.Type == MariaDbElementTypes.SqlTable
                && string.Equals(i.Name, referenced.Name, StringComparison.Ordinal));

            if (referencedTable is not null)
            {
                yield return new CreateDependency(referencedTable, foreignKey);
            }
        }
    }

    public Element NormalizeForComparison(Element source, Element target) => source;

    public IList<Element>? GetDependentElements(Element sourceElement, Model model)
    {
        if (sourceElement.Type != MariaDbElementTypes.SqlTable
            || sourceElement.Name is not string tableName)
        {
            return null;
        }

        var deps = new List<Element>();

        foreach (var element in model.Elements.Where(i => IsDependentElementType(i.Type)))
        {
            var tableRelationshipName = element.Type == MariaDbElementTypes.SqlIndex
                ? MariaDbRelationshipNames.IndexedObject
                : MariaDbRelationshipNames.DefiningTable;

            var tableRelationship = element.GetRelationship(tableRelationshipName);
            var reference = tableRelationship?.GetReference(tableName);

            if (reference != null)
            {
                deps.Add(element);
            }
        }

        return deps;
    }
}

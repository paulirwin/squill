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

    // MariaDB routines are not modeled yet, so no element type is replaced wholesale.
    public bool IsReplaceableElementType(string type) => false;

    public bool DropCausesDataLoss(string type)
        => type == MariaDbElementTypes.SqlTable;

    public bool IsDroppableStandaloneDependent(string type)
        => type == MariaDbElementTypes.SqlIndex;

    // MariaDB objects are not schema-scoped within a database (the database is the schema),
    // so no element carries a schema for identity.
    public string? GetElementSchema(Element element) => null;

    // MariaDB has no schema/extension objects that must precede tables, so every element
    // sorts to the same create order.
    public int GetCreateOrder(string type) => 0;

    // MariaDB has no extension version to normalize, so the source is compared as-is.
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

using Squill.Core;

namespace Squill.Provider.Postgres;

public class PostgresDatabaseDependencyAnalyzer : IDatabaseDependencyAnalyzer
{
    public bool IsDependentElementType(string type)
        => type is PostgresElementTypes.SqlPrimaryKeyConstraint
            or PostgresElementTypes.SqlIndex
            or PostgresElementTypes.SqlForeignKeyConstraint;

    public bool IsTableElementType(string type)
        => type == PostgresElementTypes.SqlTable;

    public bool DropCausesDataLoss(string type)
        => type == PostgresElementTypes.SqlTable;

    public bool IsDroppableStandaloneDependent(string type)
        => type == PostgresElementTypes.SqlIndex;

    public IList<Element>? GetDependentElements(Element sourceElement, Model model)
    {
        if (sourceElement.Type != PostgresElementTypes.SqlTable
            || sourceElement.Name is not string tableName)
        {
            return null;
        }

        var deps = new List<Element>();

        foreach (var element in model.Elements.Where(i => IsDependentElementType(i.Type)))
        {
            var tableRelationshipName = element.Type == PostgresElementTypes.SqlIndex
                ? PostgresRelationshipNames.IndexedObject
                : PostgresRelationshipNames.DefiningTable;

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
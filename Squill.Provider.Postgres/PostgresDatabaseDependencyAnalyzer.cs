using Squill.Core;

namespace Squill.Provider.Postgres;

public class PostgresDatabaseDependencyAnalyzer : IDatabaseDependencyAnalyzer
{
    public bool IsDependentElementType(string type) 
        => type == PostgresElementTypes.SqlPrimaryKeyConstraint;

    public IList<Element>? GetDependentElements(Element sourceElement, Model model)
    {
        if (sourceElement.Type != PostgresElementTypes.SqlTable
            || sourceElement.Name is not string tableName)
        {
            return null;
        }

        var deps = new List<Element>();

        foreach (var pkConstraint in model.Elements.Where(i => i.Type.Equals(PostgresElementTypes.SqlPrimaryKeyConstraint)))
        {
            var definingTable = pkConstraint.GetRelationship(PostgresRelationshipNames.DefiningTable);
            var reference = definingTable?.GetReference(tableName);

            if (reference != null)
            {
                deps.Add(pkConstraint);
            }
        }
        
        return deps;
    }
}
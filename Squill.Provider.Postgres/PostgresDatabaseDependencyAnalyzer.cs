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

    public bool IsExtensionElementType(string type)
        => type == PostgresElementTypes.SqlExtension;

    public string? GetExtensionVersion(Element extension)
        => extension.GetProperty<string>(PostgresPropertyNames.Version);

    public bool DropCausesDataLoss(string type)
        => type == PostgresElementTypes.SqlTable;

    public bool IsDroppableStandaloneDependent(string type)
        => type == PostgresElementTypes.SqlIndex;

    public string? GetElementSchema(Element element)
    {
        // Tables and indexes carry their schema in a Schema relationship. Extensions and
        // schemas themselves are not schema-scoped (an extension is globally named per
        // database; a schema is the namespace), so they have no schema for identity.
        if (element.Type is not (PostgresElementTypes.SqlTable or PostgresElementTypes.SqlIndex))
        {
            return null;
        }

        return PostgresModelFactory.GetSchema(element);
    }

    public int GetCreateOrder(string type) => type switch
    {
        // A schema must exist before the tables (and other objects) that live in it, and an
        // extension before a table that uses one of its types. Tables (and everything else)
        // come after both.
        PostgresElementTypes.SqlSchema => 0,
        PostgresElementTypes.SqlExtension => 1,
        _ => 2,
    };

    public IEnumerable<CreateDependency> GetCreateDependencies(Element element, Model model)
    {
        // Only a table orders against other elements, and it does so through the foreign
        // keys defined on it — which are separate elements referencing it as their
        // defining table.
        if (element.Type != PostgresElementTypes.SqlTable || element.Name is not string tableName)
        {
            yield break;
        }

        var schema = GetElementSchema(element) ?? "public";

        foreach (var foreignKey in model.Elements.Where(i =>
                     i.Type == PostgresElementTypes.SqlForeignKeyConstraint))
        {
            // The FK belongs to this table only if its defining table matches by name and
            // schema — two same-named tables in different schemas each have their own.
            if (foreignKey.GetRelationship(PostgresRelationshipNames.DefiningTable)
                    ?.GetReference(tableName) is null)
            {
                continue;
            }

            if (!string.Equals(GetForeignKeySchema(foreignKey, model, tableName), schema, StringComparison.Ordinal))
            {
                continue;
            }

            var referenced = foreignKey.GetRelationship(PostgresRelationshipNames.ForeignTable)
                ?.Entries.OfType<Reference>().FirstOrDefault();

            if (referenced is null)
            {
                continue;
            }

            if (ResolveTable(referenced.Name, model) is { } referencedTable)
            {
                yield return new CreateDependency(referencedTable, foreignKey);
            }
        }
    }

    // The schema of the table an FK is defined on. The FK carries no schema of its own, so
    // it is taken from the table it names — unambiguous unless two same-named tables exist
    // in different schemas, in which case the FK can't be attributed by name alone and the
    // table's own schema is assumed (matching GetDependentElements' handling).
    private static string GetForeignKeySchema(Element foreignKey, Model model, string tableName)
    {
        var candidates = model.Elements
            .Where(i => i.Type == PostgresElementTypes.SqlTable
                && string.Equals(i.Name, tableName, StringComparison.Ordinal))
            .ToList();

        return candidates.Count == 1
            ? PostgresModelFactory.GetSchema(candidates[0]) ?? "public"
            : PostgresModelFactory.GetSchema(foreignKey) ?? "public";
    }

    // Resolves a foreign key's referenced-table name to its table element. The name is
    // bare for a table in the public schema and schema-qualified otherwise (see the model
    // builders' NormalizeReferencedTable), so both forms must be handled.
    private static Element? ResolveTable(string referencedName, Model model)
    {
        var segments = referencedName.Split('.');

        var (schema, bareName) = segments.Length > 1
            ? (segments[0], segments[^1])
            : ("public", segments[0]);

        return model.Elements.FirstOrDefault(i =>
            i.Type == PostgresElementTypes.SqlTable
            && string.Equals(i.Name, bareName, StringComparison.Ordinal)
            && string.Equals(PostgresModelFactory.GetSchema(i) ?? "public", schema, StringComparison.Ordinal));
    }

    public Element NormalizeForComparison(Element source, Element target)
    {
        // Only extensions need normalization, and only when the source pins no version:
        // the database always reports an installed version, so an unpinned source would
        // otherwise look different. Backfill the target's version onto a copy so an
        // unmanaged extension hash-matches. A source that DOES pin a version keeps it, so a
        // version difference is preserved and surfaces as an ALTER.
        if (source.Type != PostgresElementTypes.SqlExtension
            || target.Type != PostgresElementTypes.SqlExtension)
        {
            return source;
        }

        var sourceVersion = source.GetProperty<string>(PostgresPropertyNames.Version);

        if (sourceVersion != null)
        {
            return source;
        }

        var targetVersion = target.GetProperty<string>(PostgresPropertyNames.Version);

        if (targetVersion == null)
        {
            return source;
        }

        // Copy the extension and stamp the target's version so the comparison treats the
        // unmanaged version as already-desired. The original source element is untouched.
        var copy = new Element(source.Type) { Name = source.Name };

        foreach (var property in source.Properties)
        {
            copy.Properties.Add(property);
        }

        copy.Properties.Add(new Property(PostgresPropertyNames.Version, targetVersion));

        return copy;
    }

    public IList<Element>? GetDependentElements(Element sourceElement, Model model)
    {
        if (sourceElement.Type != PostgresElementTypes.SqlTable
            || sourceElement.Name is not string tableName)
        {
            return null;
        }

        var tableSchema = GetElementSchema(sourceElement);

        // Whether another table in the model shares this table's bare name (in a different
        // schema). When so, a schema-less dependent (PK/FK) can't be attributed to one of
        // them by name alone, so it is left off to avoid a cross-schema mis-attachment.
        var tableNameIsAmbiguous = model.Elements.Count(i =>
            i.Type == PostgresElementTypes.SqlTable && string.Equals(i.Name, tableName, StringComparison.Ordinal)) > 1;

        var deps = new List<Element>();

        foreach (var element in model.Elements.Where(i => IsDependentElementType(i.Type)))
        {
            var tableRelationshipName = element.Type == PostgresElementTypes.SqlIndex
                ? PostgresRelationshipNames.IndexedObject
                : PostgresRelationshipNames.DefiningTable;

            var tableRelationship = element.GetRelationship(tableRelationshipName);
            var reference = tableRelationship?.GetReference(tableName);

            if (reference == null)
            {
                continue;
            }

            // The reference is by bare table name, so two same-named tables in different
            // schemas would otherwise each pull in the other's dependents. An index carries
            // its own schema — require it to match the table's. A schema-less dependent
            // (PK/FK) is only attached when the table's bare name is unambiguous.
            var dependentSchema = GetElementSchema(element);

            var matches = dependentSchema != null
                ? string.Equals(dependentSchema, tableSchema, StringComparison.Ordinal)
                : !tableNameIsAmbiguous;

            if (matches)
            {
                deps.Add(element);
            }
        }

        return deps;
    }
}
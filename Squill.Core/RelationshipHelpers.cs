namespace Squill.Core;

/// <summary>
/// Shared read-side helpers that walk the model's relationships to project column names and
/// specifications. These are pure structure traversals over the provider-neutral element/
/// relationship vocabulary (<see cref="SqlElementTypes"/> / <see cref="SqlRelationshipNames"/>),
/// so they are identical across providers — the script generators, the diff analyzer, and the
/// dependency analyzer all read the same shapes. Centralizing them keeps that traversal (and,
/// for ordered columns, its hash-critical ordering) defined once.
/// </summary>
public static class RelationshipHelpers
{
    /// <summary>
    /// A table's columns in declaration order, as (canonical name, element) pairs. The order is
    /// the model's column order, which the Merkle hash depends on — callers must preserve it.
    /// </summary>
    public static IList<(string Name, Element Column)> GetOrderedColumns(Element table)
    {
        var columns = new List<(string, Element)>();

        foreach (var columnRelationship in table.Relationships
                     .Where(i => i.Name == SqlRelationshipNames.Columns))
        {
            foreach (var column in columnRelationship.Entries.OfType<Element>()
                         .Where(i => i.Type == SqlElementTypes.SqlSimpleColumn))
            {
                if (column.Name is string name)
                {
                    columns.Add((name, column));
                }
            }
        }

        return columns;
    }

    /// <summary>The canonical names of a table's columns, in declaration order.</summary>
    public static IEnumerable<string> GetOrderedColumnNames(Element table)
        => GetOrderedColumns(table).Select(c => c.Name);

    /// <summary>
    /// The bare column identifiers, in order, from a reference relationship. References store
    /// table-qualified names (e.g. orders.customer_id); a constraint clause needs just the bare
    /// column names. Quote-independent, so it lives in Core.
    /// </summary>
    public static IList<string> GetReferenceColumnNames(Element element, string relationshipName)
    {
        var relationship = element.GetRelationship(relationshipName);

        if (relationship == null)
        {
            return new List<string>();
        }

        return relationship.Entries
            .OfType<Reference>()
            .Select(r => Unqualified(r.Name))
            .ToList();
    }

    /// <summary>
    /// The <see cref="SqlElementTypes.SqlIndexedColumnSpecification"/> entries of an index or
    /// key constraint, in order, from its ColumnSpecifications relationship.
    /// </summary>
    public static IEnumerable<Element> GetColumnSpecifications(Element indexOrKey)
    {
        var columnSpecs = indexOrKey.GetRelationship(SqlRelationshipNames.ColumnSpecifications)
            ?? throw new InvalidOperationException($"{indexOrKey.Name} has no column specifications");

        return columnSpecs.Entries.OfType<Element>()
            .Where(i => i.Type == SqlElementTypes.SqlIndexedColumnSpecification);
    }

    /// <summary>
    /// The key/index columns, in order, of an index, primary key, or unique key — walking each
    /// column specification to its column reference. Throws if a specification has no reference.
    /// </summary>
    public static IList<string> GetKeyColumns(Element indexOrKey)
    {
        var columns = new List<string>();

        foreach (var spec in GetColumnSpecifications(indexOrKey))
        {
            var column = spec.GetRelationship(SqlRelationshipNames.Column)
                ?.Entries.OfType<Reference>().SingleOrDefault()
                ?? throw new InvalidOperationException("Key column specification has no column reference");

            columns.Add(column.Name);
        }

        return columns;
    }

    // The last segment of a canonical (unquoted, dot-joined) name.
    private static string Unqualified(string canonical) => canonical.Split('.')[^1];
}

namespace Squill.Core;

public interface IDatabaseDependencyAnalyzer
{
    bool IsDependentElementType(string type);

    /// <summary>
    /// Whether <paramref name="type"/> is a top-level table element — the element type
    /// that supports in-place ALTER and table-rebuild diffing.
    /// </summary>
    bool IsTableElementType(string type);

    /// <summary>
    /// Whether dropping an element of this type destroys data (e.g. a table's rows), as
    /// opposed to a metadata-only object like an index or extension. Used to decide
    /// whether a drop is blocked by <see cref="DeployOptions.BlockOnPossibleDataLoss"/>.
    /// </summary>
    bool DropCausesDataLoss(string type);

    /// <summary>
    /// Whether a dependent element of this type can be dropped on its own while its parent
    /// table remains — an index — as opposed to a constraint (PK/FK) whose drop is a
    /// constraint-ALTER concern handled elsewhere.
    /// </summary>
    bool IsDroppableStandaloneDependent(string type);

    /// <summary>
    /// The schema (namespace) an element belongs to, for element identity — so two
    /// same-named objects in different schemas are distinct. Returns <c>null</c> for
    /// element types that are not schema-scoped (e.g. an extension, or a schema itself).
    /// The schema is part of a database's object identity but is stored provider-specific
    /// (a relationship, a name segment, …), so reading it lives behind the provider.
    /// </summary>
    string? GetElementSchema(Element element);

    /// <summary>
    /// A relative rank for creating an element of this type, so deploy steps run in
    /// dependency order — a lower rank is created first (e.g. a schema before the tables in
    /// it, an extension before a table using its types). Drops run in the reverse of this
    /// order. Elements of equal rank keep their existing relative order.
    /// </summary>
    int GetCreateOrder(string type);

    IList<Element>? GetDependentElements(Element sourceElement, Model model);
}
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
    /// Whether <paramref name="type"/> is an extension element, which supports an in-place
    /// version update (<c>ALTER EXTENSION ... UPDATE</c>) rather than a rebuild.
    /// </summary>
    bool IsExtensionElementType(string type);

    /// <summary>
    /// The version pinned on an extension element, or <c>null</c> if none is pinned. Reading
    /// it lives behind the provider because how a version is stored is database-specific.
    /// </summary>
    string? GetExtensionVersion(Element extension);

    /// <summary>
    /// Whether an element of this type is replaced wholesale when its definition changes,
    /// rather than altered facet by facet — a stored procedure, whose body is redefined in
    /// one statement (<c>CREATE OR REPLACE PROCEDURE</c>). A change to one of these
    /// produces a <see cref="RecreateDelta"/>.
    /// </summary>
    bool IsReplaceableElementType(string type);

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

    /// <summary>
    /// Returns a copy of <paramref name="source"/> adjusted for comparison against its
    /// matching <paramref name="target"/> in the database, or <paramref name="source"/>
    /// itself when no adjustment is needed. This lets a provider neutralize a facet that
    /// the database always reports but the source leaves unmanaged — e.g. an extension's
    /// installed version, which is backfilled from the target when the source pins none, so
    /// an unpinned extension still hash-matches. The original element is never mutated.
    /// </summary>
    Element NormalizeForComparison(Element source, Element target);
}
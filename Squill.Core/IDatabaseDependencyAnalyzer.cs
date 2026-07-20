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

    IList<Element>? GetDependentElements(Element sourceElement, Model model);
}
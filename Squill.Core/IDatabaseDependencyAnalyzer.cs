namespace Squill.Core;

public interface IDatabaseDependencyAnalyzer
{
    bool IsDependentElementType(string type);

    /// <summary>
    /// Whether <paramref name="type"/> is a top-level table element — the element type
    /// that supports in-place ALTER and table-rebuild diffing.
    /// </summary>
    bool IsTableElementType(string type);

    IList<Element>? GetDependentElements(Element sourceElement, Model model);
}
namespace Squill.Core;

public interface IDatabaseDependencyAnalyzer
{
    bool IsDependentElementType(string type);
    
    IList<Element>? GetDependentElements(Element sourceElement, Model model);
}
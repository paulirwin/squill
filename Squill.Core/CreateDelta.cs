namespace Squill.Core;

public class CreateDelta : SchemaDelta
{
    public CreateDelta(Element element)
    {
        Element = element;
    }

    public Element Element { get; }

    public IList<Element> DependentElements { get; } = new List<Element>();
}
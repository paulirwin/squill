namespace Squill.Core;

public class SchemaCompare
{
    public static SchemaComparison Compare(IDatabaseProvider provider, Model source, Model target)
    {
        var comparison = new SchemaComparison();
        var analyzer = provider.DependencyAnalyzer;
        
        if (HashUtility.HashesEqual(source.Hash, target.Hash))
        {
            return comparison;
        }

        foreach (var sourceElement in source.Elements)
        {
            // HACK.PI: PKs are dependent on their table
            if (analyzer.IsDependentElementType(sourceElement.Type))
            {
                continue;
            }

            if (target.Elements.SingleOrDefault(i =>
                    i.Type.Equals(sourceElement.Type)
                    && i.Name?.Equals(sourceElement.Name) != false) is Element targetElement)
            {
                throw new NotImplementedException("Support ALTER");
            }

            var createDelta = new CreateDelta(sourceElement);
            comparison.Deltas.Add(createDelta);

            var dependentElements = analyzer.GetDependentElements(sourceElement, source);

            if (dependentElements != null)
            {
                foreach (var dependentElement in dependentElements)
                {
                    createDelta.DependentElements.Add(dependentElement);
                }
            }
        }
        
        // TODO: support DROP

        return comparison;
    }
}
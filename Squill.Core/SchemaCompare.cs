namespace Squill.Core;

public class SchemaCompare
{
    /// <param name="allowTableRebuild">
    /// When <c>true</c> (the default), a table change that can't be expressed with an
    /// in-place ALTER is deployed by rebuilding the table. When <c>false</c>, such a
    /// change throws <see cref="TableRebuildNotAllowedException"/> instead — mirroring
    /// SSDT's option to block costly data-motion operations unless explicitly permitted.
    /// </param>
    public static SchemaComparison Compare(
        IDatabaseProvider provider, Model source, Model target, bool allowTableRebuild = true)
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
                // The element exists in both models. If their hashes match, it is
                // unchanged and needs no delta; otherwise produce an ALTER (or, when an
                // in-place ALTER can't express the change, a table rebuild).
                if (HashUtility.HashesEqual(sourceElement.Hash, targetElement.Hash))
                {
                    continue;
                }

                var alterDelta = DiffExistingElement(
                    provider, sourceElement, targetElement, source, target, allowTableRebuild);

                if (alterDelta != null)
                {
                    comparison.Deltas.Add(alterDelta);
                }

                continue;
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

    // Produces the delta for an element present in both models whose definitions differ.
    // Currently only tables support in-place alteration; other element types (indexes,
    // extensions, foreign keys) are not yet diffable and throw so the gap is explicit
    // rather than silently skipped.
    private static SchemaDelta? DiffExistingElement(
        IDatabaseProvider provider,
        Element sourceElement,
        Element targetElement,
        Model sourceModel,
        Model targetModel,
        bool allowTableRebuild)
    {
        if (provider.DependencyAnalyzer.IsTableElementType(sourceElement.Type))
        {
            return provider.TableDiffAnalyzer.DiffTable(
                sourceElement, targetElement, sourceModel, targetModel, allowTableRebuild);
        }

        throw new NotImplementedException(
            $"Altering an existing {sourceElement.Type} is not yet supported.");
    }
}
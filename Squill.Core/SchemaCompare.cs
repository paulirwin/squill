namespace Squill.Core;

public class SchemaCompare
{
    /// <summary>
    /// Compares the source (desired) model against the target (current) model and produces
    /// the deltas needed to bring the target in line with the source, honoring the given
    /// <paramref name="options"/>.
    /// </summary>
    public static SchemaComparison Compare(
        IDatabaseProvider provider, Model source, Model target, DeployOptions? options = null)
    {
        options ??= DeployOptions.Default;

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
                    provider, sourceElement, targetElement, source, target,
                    options.AllowTableRebuild);

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

        // Drop standalone objects present in the target but absent from the source — only
        // when explicitly opted into, since dropping objects is destructive (SSDT's
        // DropObjectsNotInSource, off by default). Dropping a column from a still-present
        // table is handled by that table's ALTER above, not here, so it is never gated by
        // this option.
        if (options.DropObjectsNotInSource)
        {
            AddDropDeltas(comparison, analyzer, source, target);
        }

        // Record any data-loss reasons on the comparison so a caller can surface them
        // (e.g. on a dry run). Enforcement — throwing PossibleDataLossException when
        // BlockOnPossibleDataLoss is on — is left to the caller via
        // SchemaComparison.ThrowIfDataLoss, so a dry run can still preview the script.
        CollectDataLossReasons(comparison);

        if (options.BlockOnPossibleDataLoss)
        {
            comparison.ThrowIfDataLoss();
        }

        return comparison;
    }

    // Adds a DropDelta for each droppable target element that has no counterpart in the
    // source. Covers top-level objects (tables, extensions) and standalone indexes. A
    // dependent whose parent table is also being dropped is not dropped on its own — it
    // goes away with the table's DROP ... CASCADE. Reconciling a standalone constraint
    // (PK/FK) change is out of scope here.
    private static void AddDropDeltas(
        SchemaComparison comparison, IDatabaseDependencyAnalyzer analyzer, Model source, Model target)
    {
        foreach (var targetElement in target.Elements)
        {
            // Skip dependent constraints (PK/FK); their lifecycle follows their table or a
            // (not-yet-supported) constraint ALTER. Indexes are the one dependent type we
            // drop standalone, so they fall through to the existence check below.
            if (analyzer.IsDependentElementType(targetElement.Type)
                && !analyzer.IsDroppableStandaloneDependent(targetElement.Type))
            {
                continue;
            }

            var existsInSource = source.Elements.Any(i =>
                i.Type.Equals(targetElement.Type)
                && i.Name?.Equals(targetElement.Name) != false);

            if (!existsInSource)
            {
                comparison.Deltas.Add(
                    new DropDelta(targetElement, analyzer.DropCausesDataLoss(targetElement.Type)));
            }
        }
    }

    // Records a reason for each delta that would destroy data: dropping a table, dropping
    // a column from an altered table, or a rebuild that drops a column. A rebuild that only
    // reorders columns (copying every row losslessly) is not data loss and is not recorded.
    private static void CollectDataLossReasons(SchemaComparison comparison)
    {
        foreach (var delta in comparison.Deltas)
        {
            switch (delta)
            {
                case DropDelta { CausesDataLoss: true } drop:
                    comparison.DataLossReasons.Add(
                        $"dropping {Describe(drop.Element)} destroys its data");
                    break;

                case RebuildTableDelta { DropsData: true } rebuild:
                    comparison.DataLossReasons.Add(
                        $"rebuilding {Describe(rebuild.SourceElement)} drops one or more columns");
                    break;

                case AlterDelta alter:
                    foreach (var change in alter.ColumnChanges)
                    {
                        if (change.Kind == ColumnChangeKind.Drop)
                        {
                            comparison.DataLossReasons.Add(
                                $"dropping column '{change.ColumnName}' from "
                                + $"{Describe(alter.SourceElement)} destroys its data");
                        }
                    }

                    break;
            }
        }
    }

    private static string Describe(Element element) => $"{element.Type} '{element.Name}'";

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
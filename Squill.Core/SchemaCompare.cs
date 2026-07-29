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
        options ??= DeployOptions.CreateDefault();

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

            if (target.Elements.SingleOrDefault(i => ElementsMatch(analyzer, i, sourceElement))
                is Element targetElement)
            {
                // Normalize the source against its target before comparing, so a facet the
                // database always reports but the source leaves unmanaged (e.g. an
                // extension's installed version) does not read as a spurious change.
                var normalizedSource = analyzer.NormalizeForComparison(sourceElement, targetElement);

                // The element exists in both models. If they are equivalent it is unchanged and
                // needs no delta; otherwise produce an ALTER (or, when an in-place ALTER can't
                // express the change, a table rebuild).
                if (ElementComparison.AreEquivalent(analyzer, sourceElement, targetElement))
                {
                    continue;
                }

                var alterDelta = DiffExistingElement(
                    provider, normalizedSource, targetElement, source, target,
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

        // Recreate standalone dependents (indexes) that exist in both models but whose
        // definition changed. These are skipped by the loop above (they are dependent
        // elements), and a change to one doesn't alter its table's hash, so it would
        // otherwise go undetected. Postgres has no ALTER for an index definition change, so
        // the fix is drop-and-recreate.
        AddRecreateDeltas(comparison, analyzer, source, target);

        // Drop standalone objects present in the target but absent from the source — only
        // when explicitly opted into, since dropping objects is destructive (SSDT's
        // DropObjectsNotInSource, off by default). Dropping a column from a still-present
        // table is handled by that table's ALTER above, not here, so it is never gated by
        // this option.
        if (options.DropObjectsNotInSource)
        {
            AddDropDeltas(comparison, analyzer, source, target);
        }

        OrderDeltas(comparison, analyzer, source);

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

    // Reconciles the standalone dependents (indexes, and the constraints — unique, CHECK,
    // primary key, foreign key) that the main loop skips because they are dependents, and whose
    // change does not alter their table's hash — so without this pass a change to one alone
    // would be lost. Present in both models but differing yields a RecreateDelta; present only
    // in the source, on a table that already exists, yields a CreateDelta. None holds data, so
    // this is never gated by a data-loss option.
    //
    // This runs BEFORE OrderDeltas, which is what makes the coveredByTable check below correct
    // for a circular foreign key: the constraint closing a cycle is still in its table's
    // DependentElements at this point and is only moved out into a deferred AddConstraintDelta
    // later, so it is seen as covered here and does not also get a CreateDelta of its own.
    private static void AddRecreateDeltas(
        SchemaComparison comparison, IDatabaseDependencyAnalyzer analyzer, Model source, Model target)
    {
        // Indexes whose table is being created or rebuilt are already (re)created as that
        // table's dependents, so recreating them here would double the CREATE (and emit a
        // spurious DROP). Exclude them.
        var coveredByTable = new HashSet<Element>(
            comparison.Deltas
                .SelectMany(delta => delta switch
                {
                    CreateDelta create => create.DependentElements,
                    RebuildTableDelta rebuild => rebuild.DependentElements,
                    _ => Enumerable.Empty<Element>(),
                }));

        foreach (var sourceElement in source.Elements)
        {
            if (!analyzer.IsDependentElementType(sourceElement.Type)
                || !analyzer.IsDroppableStandaloneDependent(sourceElement.Type))
            {
                continue;
            }

            if (coveredByTable.Contains(sourceElement))
            {
                continue;
            }

            if (target.Elements.SingleOrDefault(i => ElementsMatch(analyzer, i, sourceElement))
                is not Element targetElement)
            {
                // Absent in the target and not covered by a table being created above, so
                // its table already exists: there is no CREATE TABLE to carry it and nothing
                // to recreate, so it needs a CreateDelta of its own (e.g. adding an index or
                // a unique constraint to an existing table). Without this the object would
                // simply never be created.
                comparison.Deltas.Add(new CreateDelta(sourceElement));
                continue;
            }

            // Normalized both ways, so a facet only one side can express — a CHECK predicate the
            // target could canonicalize but the source could not, or the reverse — does not read
            // as a change (issue #156).
            if (ElementComparison.AreEquivalent(analyzer, sourceElement, targetElement))
            {
                continue;
            }

            comparison.Deltas.Add(new RecreateDelta(sourceElement, targetElement));
        }
    }

    // Adds a DropDelta for each droppable target element that has no counterpart in the
    // source. Covers top-level objects (tables, extensions) and every standalone dependent —
    // indexes and constraints alike, including the primary and foreign keys that were formerly
    // skipped here and so silently outlived their removal from the source (issue #157).
    //
    // A dependent whose table is being dropped too is left alone: the table's DROP takes it
    // along, so dropping it separately would be a redundant statement against an object that is
    // about to disappear.
    private static void AddDropDeltas(
        SchemaComparison comparison, IDatabaseDependencyAnalyzer analyzer, Model source, Model target)
    {
        // The tables going away in this deploy, by name, so a dependent can tell whether its
        // own table outlives it.
        var droppedTables = target.Elements
            .Where(i => analyzer.IsTableElementType(i.Type)
                && i.Name is not null
                && !source.Elements.Any(j => ElementsMatch(analyzer, j, i)))
            .Select(i => (string)i.Name!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var targetElement in target.Elements)
        {
            if (analyzer.IsDependentElementType(targetElement.Type))
            {
                // A dependent type a provider does not reconcile standalone still follows its
                // table. Nothing declares itself that way today, but the seam is what a
                // provider would use to opt one out.
                if (!analyzer.IsDroppableStandaloneDependent(targetElement.Type))
                {
                    continue;
                }

                if (GetOwningTableName(targetElement) is { } owningTable
                    && droppedTables.Contains(owningTable))
                {
                    continue;
                }
            }

            var existsInSource = source.Elements.Any(i => ElementsMatch(analyzer, i, targetElement));

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

    // Orders the deltas so deploy steps run in dependency order: creates first (a schema
    // before the tables in it, an extension before a table using it), then in-place changes,
    // then drops in the reverse of the create order (a table before the schema that holds
    // it). A stable ordering preserves the existing relative order within each rank.
    private static void OrderDeltas(
        SchemaComparison comparison, IDatabaseDependencyAnalyzer analyzer, Model source)
    {
        // Phase groups: creates (0) run before alters/rebuilds (1) before drops (2).
        static int Phase(SchemaDelta delta) => delta switch
        {
            CreateDelta => 0,
            DropDelta => 2,
            _ => 1,
        };

        // Within creates, ascending create-rank; within drops, descending; alters keep 0.
        int Rank(SchemaDelta delta) => delta switch
        {
            CreateDelta create => analyzer.GetCreateOrder(create.Element.Type),
            DropDelta drop => -analyzer.GetCreateOrder(drop.Element.Type),
            _ => 0,
        };

        var ordered = comparison.Deltas
            .Select((delta, index) => (delta, index))
            .OrderBy(x => Phase(x.delta))
            .ThenBy(x => Rank(x.delta))
            .ThenBy(x => x.index)
            .Select(x => x.delta)
            .ToList();

        // Rank alone can't order two elements of the same type, so a table whose foreign
        // key references another table may still precede it. Sort each create rank
        // topologically to fix that, leaving the ranks themselves (and every other phase)
        // where they are. A circular reference cannot be ordered at all, so the constraint
        // closing the cycle is pulled out here to be added after every table exists.
        var deferred = new List<AddConstraintDelta>();

        ordered = SortCreatesByDependency(ordered, analyzer, source, Phase, Rank, deferred);

        // Deferred constraints go after all creates but before alters and drops, so the
        // tables they join are guaranteed to exist by the time they run.
        if (deferred.Count > 0)
        {
            var afterCreates = ordered.FindLastIndex(i => Phase(i) == 0) + 1;

            ordered.InsertRange(afterCreates, deferred);
        }

        comparison.Deltas.Clear();

        foreach (var delta in ordered)
        {
            comparison.Deltas.Add(delta);
        }
    }

    // Reorders the creates within each rank so an element follows everything it depends on
    // (a table follows the tables its foreign keys reference). Only same-rank creates are
    // permuted: the rank ordering already encodes coarse dependencies (schema before table)
    // and must not be disturbed.
    private static List<SchemaDelta> SortCreatesByDependency(
        List<SchemaDelta> ordered,
        IDatabaseDependencyAnalyzer analyzer,
        Model source,
        Func<SchemaDelta, int> phase,
        Func<SchemaDelta, int> rank,
        List<AddConstraintDelta> deferred)
    {
        var result = new List<SchemaDelta>(ordered.Count);
        var position = 0;

        while (position < ordered.Count)
        {
            var delta = ordered[position];

            if (phase(delta) != 0)
            {
                result.Add(delta);
                position++;
                continue;
            }

            // Take the whole run of creates sharing this rank and sort it as one group.
            var currentRank = rank(delta);
            var group = new List<CreateDelta>();

            while (position < ordered.Count
                   && ordered[position] is CreateDelta create
                   && phase(ordered[position]) == 0
                   && rank(ordered[position]) == currentRank)
            {
                group.Add(create);
                position++;
            }

            result.AddRange(TopologicalSort(group, analyzer, source, deferred));
        }

        return result;
    }

    // Depth-first topological sort, preserving the input order wherever dependencies allow
    // so the result stays stable and predictable.
    //
    // When a cycle is found (two tables referencing each other), the constraint that closes
    // it is deferred: it is removed from its table's inline definition and collected into
    // <paramref name="deferred"/> so the caller can add it with ALTER TABLE once every
    // table exists. No create order could satisfy a cycle, so breaking one edge is the only
    // way to deploy it at all.
    //
    // A self-reference is a cycle of one but needs no deferral — a table may reference
    // itself in the same CREATE — so it is skipped rather than broken.
    private static List<CreateDelta> TopologicalSort(
        List<CreateDelta> group,
        IDatabaseDependencyAnalyzer analyzer,
        Model source,
        List<AddConstraintDelta> deferred)
    {
        if (group.Count < 2)
        {
            return group;
        }

        // Only elements in this group can be ordered against each other; a dependency on
        // something already created (or in another rank) needs no edge.
        var byElement = new Dictionary<Element, CreateDelta>();

        foreach (var create in group)
        {
            byElement.TryAdd(create.Element, create);
        }

        var sorted = new List<CreateDelta>(group.Count);

        // null = unvisited, false = in progress (revisiting one means a cycle), true = done.
        var state = new Dictionary<CreateDelta, bool>();

        void Visit(CreateDelta create)
        {
            if (state.ContainsKey(create))
            {
                return;
            }

            state[create] = false;

            foreach (var (dependsOn, constraint) in analyzer.GetCreateDependencies(create.Element, source))
            {
                // Skip a self-reference and anything outside this group.
                if (!byElement.TryGetValue(dependsOn, out var dependencyCreate)
                    || ReferenceEquals(dependencyCreate, create))
                {
                    continue;
                }

                // The dependency is still in progress further up this path, so following it
                // would close a cycle. Defer the constraint that forms this edge instead,
                // which lets both tables be created and the constraint added afterwards.
                if (state.TryGetValue(dependencyCreate, out var finished) && !finished)
                {
                    if (constraint is not null && create.DependentElements.Remove(constraint))
                    {
                        deferred.Add(new AddConstraintDelta(constraint, create.Element));
                    }

                    continue;
                }

                Visit(dependencyCreate);
            }

            state[create] = true;
            sorted.Add(create);
        }

        foreach (var create in group)
        {
            Visit(create);
        }

        return sorted;
    }

    // Whether two elements denote the same database object: same type, same name, and —
    // for schema-scoped types — same schema, so two same-named objects in different
    // schemas (public.foo vs. staging.foo) are distinct. A null name never matches (an
    // anonymous element is not identity-comparable), so it can't act as a wildcard.
    //
    // A dependent (an index, a constraint) is additionally scoped by the table it belongs to.
    // Its name is only unique within that table, not across the database: MariaDB's Sakila has
    // an `idx_fk_film_id` on both film_actor and inventory, and an `idx_fk_address_id` on three
    // tables. Without the table in the comparison those all read as one object, and matching a
    // source index against the target would find several (issue #122).
    private static bool ElementsMatch(IDatabaseDependencyAnalyzer analyzer, Element a, Element b)
    {
        if (!a.Type.Equals(b.Type))
        {
            return false;
        }

        if (a.Name is null || b.Name is null)
        {
            return false;
        }

        if (!a.Name.Equals(b.Name)
            || !string.Equals(analyzer.GetElementSchema(a), analyzer.GetElementSchema(b), StringComparison.Ordinal))
        {
            return false;
        }

        if (!analyzer.IsDependentElementType(a.Type))
        {
            return true;
        }

        return string.Equals(GetOwningTableName(a), GetOwningTableName(b), StringComparison.Ordinal);
    }

    // The name of the table a dependent element belongs to, or null when it names none. An
    // index records its table as IndexedObject; every other dependent (PK, FK, unique
    // constraint) as DefiningTable.
    private static string? GetOwningTableName(Element element)
    {
        var relationshipName = element.Type == SqlElementTypes.SqlIndex
            ? SqlRelationshipNames.IndexedObject
            : SqlRelationshipNames.DefiningTable;

        return element.GetRelationship(relationshipName)
            ?.Entries.OfType<Reference>().FirstOrDefault()?.Name;
    }

    // Produces the delta for an element present in both models whose definitions differ.
    // Tables alter in place or rebuild; an extension updates its version; a provider may
    // supply an in-place alter for its own object kinds (a Postgres enum or domain); and a
    // replaceable object is redefined wholesale. Anything else throws, so a gap is explicit
    // rather than silently skipped.
    private static SchemaDelta? DiffExistingElement(
        IDatabaseProvider provider,
        Element sourceElement,
        Element targetElement,
        Model sourceModel,
        Model targetModel,
        bool allowTableRebuild)
    {
        var analyzer = provider.DependencyAnalyzer;

        if (analyzer.IsTableElementType(sourceElement.Type))
        {
            return provider.TableDiffAnalyzer.DiffTable(
                sourceElement, targetElement, sourceModel, targetModel, allowTableRebuild);
        }

        if (analyzer.IsExtensionElementType(sourceElement.Type))
        {
            // The source is already normalized: an unpinned version was backfilled to the
            // target's, so reaching here means the source pins a version. If it differs
            // from the installed version, update to it; otherwise there is nothing to do.
            var sourceVersion = analyzer.GetExtensionVersion(sourceElement);
            var targetVersion = analyzer.GetExtensionVersion(targetElement);

            if (sourceVersion != null && !string.Equals(sourceVersion, targetVersion, StringComparison.Ordinal))
            {
                return new AlterExtensionVersionDelta(sourceElement, sourceVersion);
            }

            return null;
        }

        // Some object kinds change in place but need more than a wholesale redefinition — a
        // PostgreSQL enum gaining a label, or a domain changing base type. Neither can be
        // dropped and recreated while a column still uses it, so the provider supplies the
        // in-place delta (issue #122).
        if (analyzer.GetInPlaceAlterDelta(sourceElement, targetElement) is { } inPlaceDelta)
        {
            return inPlaceDelta;
        }

        // An object that is redefined wholesale (a procedure) needs no facet-by-facet diff:
        // the source definition simply replaces the target's.
        if (analyzer.IsReplaceableElementType(sourceElement.Type))
        {
            return new RecreateDelta(sourceElement, targetElement);
        }

        throw new NotImplementedException(
            $"Altering an existing {sourceElement.Type} is not yet supported.");
    }
}

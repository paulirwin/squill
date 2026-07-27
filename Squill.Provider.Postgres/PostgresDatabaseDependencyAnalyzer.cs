using Squill.Core;

namespace Squill.Provider.Postgres;

/// <summary>
/// Encodes PostgreSQL element dependency rules for schema comparison. The shared classification
/// and foreign-key walk live in <see cref="DatabaseDependencyAnalyzerBase"/>; this provider adds
/// the schema/extension features Postgres has and overrides the disambiguation seams so
/// same-named tables in different schemas keep their own dependents.
/// </summary>
public class PostgresDatabaseDependencyAnalyzer : DatabaseDependencyAnalyzerBase
{
    public override bool IsExtensionElementType(string type)
        => type == PostgresElementTypes.SqlExtension;

    public override string? GetExtensionVersion(Element extension)
        => extension.GetProperty<string>(PostgresPropertyNames.Version);

    // A procedure's body is replaced wholesale. A view is here too, but its recreate is
    // scripted as DROP + CREATE: PostgreSQL will not replace a view whose column list changed,
    // and a changed column list is the only thing that makes a view differ.
    //
    // An aggregate and a trigger are also DROP + CREATE (issue #122): PostgreSQL has no
    // CREATE OR REPLACE AGGREGATE at all, and CREATE OR REPLACE TRIGGER only exists from
    // PG 14, so dropping first is the portable spelling. Including triggers also aligns this
    // provider with MariaDB, which has always treated them as replaceable — before this, the
    // same trigger change deployed on MariaDB but threw on Postgres.
    public override bool IsReplaceableElementType(string type)
        => type is PostgresElementTypes.SqlProcedure
            or PostgresElementTypes.SqlView
            or PostgresElementTypes.SqlFunction
            or PostgresElementTypes.SqlAggregate
            or PostgresElementTypes.SqlTrigger;

    // An enum, a domain and a sequence are altered in place rather than replaced: DROP TYPE /
    // DROP DOMAIN fails whenever a column is typed as them, so a rebuild would break any schema
    // that actually uses the type, and dropping a sequence would reset its counter (issue #122).
    public override SchemaDelta? GetInPlaceAlterDelta(Element source, Element target)
    {
        if (source.Type == PostgresElementTypes.SqlEnumType)
        {
            return new AlterEnumTypeDelta(source, target, DiffEnumLabels(source, target));
        }

        if (source.Type == PostgresElementTypes.SqlDomain)
        {
            return new AlterDomainTypeDelta(source, target);
        }

        // Every option of a sequence is alterable, and dropping one would reset its counter
        // (and fail while a column default still calls nextval() on it), so a changed sequence
        // is altered in place too.
        if (source.Type == PostgresElementTypes.SqlSequence)
        {
            return new AlterSequenceDelta(source, target);
        }

        return null;
    }

    /// <summary>
    /// The labels present in <paramref name="source"/> but not in <paramref name="target"/>,
    /// each paired with the existing label it must precede (or null to append).
    ///
    /// Returns an empty list when the change is not purely additive — a removed or reordered
    /// label. PostgreSQL can express neither, so the script generator reports those rather
    /// than emitting SQL that would not achieve the declared state.
    /// </summary>
    private static IReadOnlyList<AddedEnumLabel> DiffEnumLabels(Element source, Element target)
    {
        var sourceLabels = PostgresModelFactory.GetEnumLabels(source);
        var targetLabels = PostgresModelFactory.GetEnumLabels(target);

        // Every existing label must survive, in its existing relative order: an enum's order
        // is its sort order, and ALTER TYPE cannot remove or move a label. If the target's
        // labels are not a subsequence of the source's, the change is not additive.
        if (!IsSubsequence(targetLabels, sourceLabels))
        {
            return [];
        }

        var existing = targetLabels.ToHashSet(StringComparer.Ordinal);
        var added = new List<AddedEnumLabel>();

        for (var i = 0; i < sourceLabels.Count; i++)
        {
            if (existing.Contains(sourceLabels[i]))
            {
                continue;
            }

            // A new label goes before the next label that already exists in the target;
            // when there is none, it belongs at the end and is appended.
            var successor = sourceLabels.Skip(i + 1).FirstOrDefault(existing.Contains);

            added.Add(new AddedEnumLabel(sourceLabels[i], successor));
        }

        return added;
    }

    // Whether every element of `inner` appears in `outer` in the same relative order.
    private static bool IsSubsequence(IReadOnlyList<string> inner, IReadOnlyList<string> outer)
    {
        var index = 0;

        foreach (var candidate in outer)
        {
            if (index < inner.Count && string.Equals(inner[index], candidate, StringComparison.Ordinal))
            {
                index++;
            }
        }

        return index == inner.Count;
    }

    public override string? GetElementSchema(Element element)
    {
        // Tables, indexes and unique constraints carry their schema in a Schema
        // relationship — a unique constraint needs one so the ALTER TABLE that adds or drops
        // it can qualify the table. Extensions and
        // schemas themselves are not schema-scoped (an extension is globally named per
        // database; a schema is the namespace), so they have no schema for identity. Enum
        // types and domains are schema-scoped user-defined types (issue #75), so they carry
        // their schema like a table does.
        if (element.Type is not (PostgresElementTypes.SqlTable
            or PostgresElementTypes.SqlIndex
            or PostgresElementTypes.SqlUniqueConstraint
            or PostgresElementTypes.SqlCheckConstraint
            or PostgresElementTypes.SqlProcedure
            or PostgresElementTypes.SqlView
            or PostgresElementTypes.SqlEnumType
            or PostgresElementTypes.SqlDomain
            or PostgresElementTypes.SqlSequence
            or PostgresElementTypes.SqlFunction
            or PostgresElementTypes.SqlAggregate
            or PostgresElementTypes.SqlTrigger))
        {
            return null;
        }

        return PostgresModelFactory.GetSchema(element);
    }

    // In addition to a view's query (excluded by the base), a domain's CHECK predicate does not
    // participate in identity: PostgreSQL rewrites it when it stores it, so the declared
    // `VALUE >= 1901 AND VALUE <= 2155` comes back from pg_get_constraintdef as
    // `((VALUE >= 1901) AND (VALUE <= 2155))` and could never hash-match. A domain's identity is
    // its name and base type instead (see PostgresModelFactory.CreateDomain). Restating the rule
    // here is what keeps it true for a model loaded from a DACPAC, where the flag itself is not
    // stored (issue #122).
    public override bool ParticipatesInIdentity(string elementType, string propertyName)
        => base.ParticipatesInIdentity(elementType, propertyName)
           && (elementType, propertyName)
               is not (PostgresElementTypes.SqlDomain, PostgresPropertyNames.CheckExpression);

    public override int GetCreateOrder(string type) => type switch
    {
        // A schema must exist before the tables (and other objects) that live in it, and an
        // extension before a table that uses one of its types. Tables (and everything else)
        // come after both.
        PostgresElementTypes.SqlSchema => 0,
        PostgresElementTypes.SqlExtension => 1,
        // A user-defined type (enum) or domain must exist before the tables whose columns are
        // typed as it — same rank as an extension: after the schema, before tables.
        PostgresElementTypes.SqlEnumType => 1,
        PostgresElementTypes.SqlDomain => 1,
        // A sequence must exist before a table whose column default draws from it via
        // nextval(), so it shares the pre-table rank (issue #122).
        PostgresElementTypes.SqlSequence => 1,
        // A function may reference any table in its body, so it is created after tables
        // (issue #81). It comes before views and aggregates, which may call it — a view that
        // uses group_concat or a plain function, or an aggregate's SFUNC, needs the function to
        // exist first. Body dependencies are not parsed, so this rank ordering is what
        // sequences those cross-object dependencies on deploy.
        PostgresElementTypes.SqlFunction => 3,
        // An aggregate references its state function (SFUNC), so it must be created after
        // functions (issue #82), and before a view that uses the aggregate.
        PostgresElementTypes.SqlAggregate => 4,
        // A view selects from tables and may call a function or aggregate (e.g. Pagila's views
        // build on the group_concat aggregate), so it is created after both. It comes before
        // procedures, whose bodies may in turn query a view.
        PostgresElementTypes.SqlView => 5,
        // A procedure body may reference any table or view, so it is created after them. Its
        // body is not parsed for dependencies, so this ordering is what makes a procedure that
        // reads or writes a table or view in the same deploy work.
        PostgresElementTypes.SqlProcedure => 6,
        // A trigger depends on both its table and the function it runs, so it is created after
        // everything else, including functions, aggregates and views (issue #83).
        PostgresElementTypes.SqlTrigger => 7,
        _ => 2,
    };

    public override Element NormalizeForComparison(Element source, Element target)
    {
        // A view needs no normalization: its query is stored as a property that opts out of the
        // element's identity (see PostgresModelFactory.CreateView), so the fact that PostgreSQL
        // rewrites it — and that an extracted view carries no query at all — never reaches the
        // comparison.

        // Only extensions need normalization, and only when the source pins no version: the
        // database always reports an installed version, so an unpinned source would otherwise
        // look different. Backfill the target's version onto a copy so an unmanaged extension
        // hash-matches. A source that DOES pin a version keeps it, so a version difference is
        // preserved and surfaces as an ALTER.
        if (source.Type != PostgresElementTypes.SqlExtension
            || target.Type != PostgresElementTypes.SqlExtension)
        {
            return source;
        }

        var sourceVersion = source.GetProperty<string>(PostgresPropertyNames.Version);

        if (sourceVersion != null)
        {
            return source;
        }

        var targetVersion = target.GetProperty<string>(PostgresPropertyNames.Version);

        if (targetVersion == null)
        {
            return source;
        }

        // Copy the extension and stamp the target's version so the comparison treats the
        // unmanaged version as already-desired. The original source element is untouched.
        var copy = new Element(source.Type) { Name = source.Name };

        foreach (var property in source.Properties)
        {
            copy.Properties.Add(property);
        }

        copy.Properties.Add(new Property(PostgresPropertyNames.Version, targetVersion));

        return copy;
    }

    // A dependent (PK/index/FK) is by bare table name, so two same-named tables in different
    // schemas would otherwise each pull in the other's dependents. An index carries its own
    // schema — require it to match the table's. A schema-less dependent (PK/FK) is only
    // attached when the table's bare name is unambiguous.
    protected override bool DependentBelongsToTable(Element dependent, Element table, Model model)
    {
        var dependentSchema = GetElementSchema(dependent);

        if (dependentSchema != null)
        {
            return string.Equals(dependentSchema, GetElementSchema(table), StringComparison.Ordinal);
        }

        var ambiguous = model.Elements.Count(i =>
            i.Type == PostgresElementTypes.SqlTable
            && string.Equals(i.Name, table.Name, StringComparison.Ordinal)) > 1;

        return !ambiguous;
    }

    // The FK belongs to this table only if its defining table matches by schema too — two
    // same-named tables in different schemas each have their own.
    protected override bool ForeignKeyBelongsToTable(Element foreignKey, Element table, Model model)
    {
        var schema = GetElementSchema(table) ?? "public";
        var tableName = table.Name!;

        return string.Equals(
            GetForeignKeySchema(foreignKey, model, tableName), schema, StringComparison.Ordinal);
    }

    // Resolves a foreign key's referenced-table name to its table element. The name is bare for
    // a table in the public schema and schema-qualified otherwise (see the model builders'
    // NormalizeReferencedTable), so both forms must be handled.
    protected override Element? ResolveReferencedTable(string referencedName, Model model)
    {
        var segments = referencedName.Split('.');

        var (schema, bareName) = segments.Length > 1
            ? (segments[0], segments[^1])
            : ("public", segments[0]);

        return model.Elements.FirstOrDefault(i =>
            i.Type == PostgresElementTypes.SqlTable
            && string.Equals(i.Name, bareName, StringComparison.Ordinal)
            && string.Equals(PostgresModelFactory.GetSchema(i) ?? "public", schema, StringComparison.Ordinal));
    }

    // The schema of the table an FK is defined on. The FK carries no schema of its own, so it
    // is taken from the table it names — unambiguous unless two same-named tables exist in
    // different schemas, in which case the FK can't be attributed by name alone and the table's
    // own schema is assumed (matching GetDependentElements' handling).
    private static string GetForeignKeySchema(Element foreignKey, Model model, string tableName)
    {
        var candidates = model.Elements
            .Where(i => i.Type == PostgresElementTypes.SqlTable
                && string.Equals(i.Name, tableName, StringComparison.Ordinal))
            .ToList();

        return candidates.Count == 1
            ? PostgresModelFactory.GetSchema(candidates[0]) ?? "public"
            : PostgresModelFactory.GetSchema(foreignKey) ?? "public";
    }
}

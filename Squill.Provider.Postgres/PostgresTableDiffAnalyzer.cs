using Squill.Core;

namespace Squill.Provider.Postgres;

/// <summary>
/// Diffs two PostgreSQL table elements (desired vs. current) and decides how to reconcile
/// them: an in-place <see cref="AlterDelta"/> of ADD / DROP / ALTER COLUMN operations when
/// the change can be expressed that way, or a <see cref="RebuildTableDelta"/> when it cannot
/// (e.g. a column inserted between existing columns, which would change the physical column
/// order). The shared diffing skeleton lives in <see cref="TableDiffAnalyzerBase"/>; this
/// type supplies the Postgres dependency analyzer and the identity-change rebuild rule.
/// </summary>
public class PostgresTableDiffAnalyzer : TableDiffAnalyzerBase
{
    protected override IDatabaseDependencyAnalyzer DependencyAnalyzer { get; } =
        new PostgresDatabaseDependencyAnalyzer();

    // An identity change (adding, removing, or switching ALWAYS/BY DEFAULT GENERATED AS
    // IDENTITY, or any sequence option — issue #13) can't be expressed by the ALTER path's
    // TYPE + nullability clauses; a rebuild recreates the column with the desired identity.
    // (Postgres could ALTER COLUMN ... SET <seqoption> in place; that optimization is future
    // work.)
    protected override bool ColumnChangeRequiresRebuild(Element source, Element target, out string reason)
    {
        if (IdentityDiffers(source, target))
        {
            reason = "changed its identity definition";
            return true;
        }

        // A column that gained or lost its generated-ness (issue #158). Neither direction can be
        // expressed by the ALTER path, which emits only TYPE, nullability, and DEFAULT clauses —
        // it would emit nothing at all, and an AlterDelta with no clauses renders to the empty
        // string the deployer then hands to Npgsql. Checked before GenerationDiffers, which
        // compares two expressions and so only speaks to columns generated on both sides.
        if (GeneratedNessDiffers(source, target))
        {
            reason = source.GetProperty<string>(PostgresPropertyNames.GeneratedExpression) is null
                ? "is no longer a generated column"
                : "became a generated column";
            return true;
        }

        // A generated column whose expression was redefined (issue #156). The ALTER path emits
        // only TYPE and nullability clauses, so it would produce nothing at all for this — the
        // change would be dropped silently. A rebuild recreates the column with the declared
        // expression, which also recomputes every existing row.
        //
        // PostgreSQL 17 added ALTER COLUMN ... SET EXPRESSION AS, which would do this in place;
        // it is a syntax error on the older majors Squill still supports (see
        // PostgresqlDatabaseSchemaProvider.SupportsSetExpression), and a rebuild reaches the same
        // end state on every one of them.
        if (GenerationDiffers(source, target))
        {
            reason = "changed its generation expression";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    // Whether one side is a generated column and the other is an ordinary one. The RAW
    // expression is what answers this: it is present whenever the column is generated, whereas
    // the normalized form is absent for any expression the normalizer could not canonicalize,
    // which would read an unchanged column as having lost its generated-ness.
    private static bool GeneratedNessDiffers(Element source, Element target)
        => (source.GetProperty<string>(PostgresPropertyNames.GeneratedExpression) is null)
            != (target.GetProperty<string>(PostgresPropertyNames.GeneratedExpression) is null);

    private static bool GenerationDiffers(Element source, Element target)
    {
        // The canonical forms are what compare: the raw expressions are spelled differently by
        // the source and the catalog even when they mean the same thing. When either has no
        // canonical form the expression is not comparable at all, so no rebuild is claimed —
        // matching the identity rule, which leaves such an expression out of the hash.
        var sourceExpression =
            source.GetProperty<string>(PostgresPropertyNames.NormalizedGeneratedExpression);
        var targetExpression =
            target.GetProperty<string>(PostgresPropertyNames.NormalizedGeneratedExpression);

        return sourceExpression is not null
            && targetExpression is not null
            && !string.Equals(sourceExpression, targetExpression, StringComparison.Ordinal);
    }

    private static bool IdentityDiffers(Element source, Element target)
    {
        var sourceIdentity = source.GetProperty<bool?>(PostgresPropertyNames.IsIdentity) == true;
        var targetIdentity = target.GetProperty<bool?>(PostgresPropertyNames.IsIdentity) == true;

        if (sourceIdentity != targetIdentity)
        {
            return true;
        }

        if (!sourceIdentity)
        {
            return false;
        }

        return source.GetProperty<string>(PostgresPropertyNames.IdentityGeneration)
                != target.GetProperty<string>(PostgresPropertyNames.IdentityGeneration)
            || source.GetProperty<long?>(PostgresPropertyNames.StartValue)
                != target.GetProperty<long?>(PostgresPropertyNames.StartValue)
            || source.GetProperty<long?>(PostgresPropertyNames.Increment)
                != target.GetProperty<long?>(PostgresPropertyNames.Increment)
            || source.GetProperty<long?>(PostgresPropertyNames.MinValue)
                != target.GetProperty<long?>(PostgresPropertyNames.MinValue)
            || source.GetProperty<long?>(PostgresPropertyNames.MaxValue)
                != target.GetProperty<long?>(PostgresPropertyNames.MaxValue)
            || source.GetProperty<long?>(PostgresPropertyNames.CacheSize)
                != target.GetProperty<long?>(PostgresPropertyNames.CacheSize)
            || source.GetProperty<bool?>(PostgresPropertyNames.IsCycling)
                != target.GetProperty<bool?>(PostgresPropertyNames.IsCycling);
    }
}

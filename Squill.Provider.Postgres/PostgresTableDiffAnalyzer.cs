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
        reason = "changed its identity definition";
        return IdentityDiffers(source, target);
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

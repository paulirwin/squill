using Squill.Core;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Diffs two MariaDB table elements (desired vs. current) and decides how to reconcile them:
/// an in-place <see cref="AlterDelta"/> of ADD / DROP / ALTER COLUMN operations when the
/// change can be expressed that way, or a <see cref="RebuildTableDelta"/> when it cannot
/// (e.g. a column inserted between existing columns, or an auto-increment change). The shared
/// diffing skeleton lives in <see cref="TableDiffAnalyzerBase"/>; this type supplies the
/// MariaDB dependency analyzer and the auto-increment-change rebuild rule.
/// </summary>
public class MariaDbTableDiffAnalyzer : TableDiffAnalyzerBase
{
    protected override IDatabaseDependencyAnalyzer DependencyAnalyzer { get; } =
        new MariaDbDatabaseDependencyAnalyzer();

    // An auto-increment change can't be expressed by the ALTER path's type + nullability
    // clauses cleanly; a rebuild recreates the column with the desired auto-increment instead
    // of silently dropping the change.
    protected override bool ColumnChangeRequiresRebuild(Element source, Element target, out string reason)
    {
        reason = "changed its auto-increment definition";
        return (source.GetProperty<bool?>(MariaDbPropertyNames.IsAutoIncrement) == true)
            != (target.GetProperty<bool?>(MariaDbPropertyNames.IsAutoIncrement) == true);
    }
}

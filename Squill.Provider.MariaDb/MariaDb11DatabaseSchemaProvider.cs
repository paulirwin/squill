using Squill.Dacpac;

namespace Squill.Provider.MariaDb;

/// <summary>
/// The schema provider for MariaDB 11. Discovered by reflection and recorded in a DACPAC
/// (via its <see cref="Squill.Dacpac.DatabaseSchemaProvider.DspName"/>) when a project targets
/// this version.
/// </summary>
public sealed class MariaDb11DatabaseSchemaProvider : MariaDbDatabaseSchemaProviderBase
{
    /// <summary>The canonical instance, discovered by reflection and cached by the registry.</summary>
    public MariaDb11DatabaseSchemaProvider()
    {
    }

    /// <summary>
    /// A per-build instance carrying the declared target floor, so a project that names a point
    /// release (MariaDB 11.x.y) gates against what it actually declared rather than this major's
    /// oldest release. The registry finds this constructor by reflection and silently falls back
    /// to the unversioned instance when it is absent, so its absence is invisible at compile time.
    /// </summary>
    public MariaDb11DatabaseSchemaProvider(TargetVersion? targetVersion)
        : base(targetVersion)
    {
    }

    public override int MajorVersion => 11;

    /// <summary>Measured on 11.8: MariaDB 11.4 moved the default to the UCA 14.0.0 collation.</summary>
    public override string DefaultCollation => "utf8mb4_uca1400_ai_ci";

    /// <summary>
    /// Measured: MariaDB's <c>information_schema.VIEWS</c> has an <c>ALGORITHM</c> column,
    /// reporting <c>MERGE</c>, <c>TEMPTABLE</c> or <c>UNDEFINED</c> per view.
    /// </summary>
    public override bool ReportsViewAlgorithm => true;
}

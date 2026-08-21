using Squill.Dacpac;

namespace Squill.Provider.MariaDb;

/// <summary>
/// The schema provider for MariaDB 10. Discovered by reflection and recorded in a DACPAC
/// (via its <see cref="Squill.Dacpac.DatabaseSchemaProvider.DspName"/>) when a project targets
/// this version.
/// </summary>
public sealed class MariaDb10DatabaseSchemaProvider : MariaDbDatabaseSchemaProviderBase
{
    /// <summary>The canonical instance, discovered by reflection and cached by the registry.</summary>
    public MariaDb10DatabaseSchemaProvider()
    {
    }

    /// <summary>
    /// A per-build instance carrying the declared target floor, so a project that names a point
    /// release (MariaDB 10.x.y) gates against what it actually declared rather than this major's
    /// oldest release. The registry finds this constructor by reflection and silently falls back
    /// to the unversioned instance when it is absent, so its absence is invisible at compile time.
    /// </summary>
    public MariaDb10DatabaseSchemaProvider(TargetVersion? targetVersion)
        : base(targetVersion)
    {
    }

    public override int MajorVersion => 10;

    /// <summary>Measured on 10.11: MariaDB 10 predates the UCA 14.0.0 collations that 11 defaults to.</summary>
    public override string DefaultCollation => "utf8mb4_general_ci";

    /// <inheritdoc />
    // Measured on 10.11: both unicode sets still default to the legacy _general_ci
    // collations, which is what makes this major differ from 11 and 12.
    protected override (string Utf8Mb3, string Utf8Mb4) DefaultUnicodeCollations
        => ("utf8mb3_general_ci", "utf8mb4_general_ci");

    /// <summary>
    /// Measured: MariaDB's <c>information_schema.VIEWS</c> has an <c>ALGORITHM</c> column,
    /// reporting <c>MERGE</c>, <c>TEMPTABLE</c> or <c>UNDEFINED</c> per view.
    /// </summary>
    public override bool ReportsViewAlgorithm => true;
}

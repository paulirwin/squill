using Squill.Dacpac;

namespace Squill.Provider.MariaDb;

/// <summary>
/// The schema provider for MySQL 8. Served by the MariaDB provider assembly (one provider
/// covers both engines), so it lives here alongside the MariaDb types. Discovered by reflection
/// and recorded in a DACPAC via its <see cref="Squill.Dacpac.DatabaseSchemaProvider.DspName"/>.
/// </summary>
public sealed class MySql8DatabaseSchemaProvider : MySqlDatabaseSchemaProviderBase
{
    /// <summary>The canonical instance, discovered by reflection and cached by the registry.</summary>
    public MySql8DatabaseSchemaProvider()
    {
    }

    /// <summary>
    /// A per-build instance carrying the declared target floor. Major 8 is where the point-release
    /// gating matters most: much of MySQL 8's DDL surface landed in 8.0.x patches, so 8.0.0 and
    /// 8.0.23 are meaningfully different targets.
    /// </summary>
    public MySql8DatabaseSchemaProvider(TargetVersion? targetVersion)
        : base(targetVersion)
    {
    }

    public override int MajorVersion => 8;

    /// <summary>Measured on 8.4: MySQL 8.0 moved the default here from latin1_swedish_ci.</summary>
    public override string DefaultCollation => "utf8mb4_0900_ai_ci";

    /// <inheritdoc />
    // Measured on 8.4. The two sets diverge here: utf8mb4 takes the 0900 collation
    // while utf8mb3 keeps the legacy _general_ci, so neither can stand in for the other.
    protected override (string Utf8Mb3, string Utf8Mb4) DefaultUnicodeCollations
        => ("utf8mb3_general_ci", "utf8mb4_0900_ai_ci");

    /// <summary>
    /// Measured: MySQL's <c>information_schema.VIEWS</c> has no <c>ALGORITHM</c> column at all,
    /// so a declared algorithm cannot be read back and is left unmodeled with a warning. The
    /// engine still honours it and echoes it from <c>SHOW CREATE VIEW</c>.
    /// </summary>
    public override bool ReportsViewAlgorithm => false;
}

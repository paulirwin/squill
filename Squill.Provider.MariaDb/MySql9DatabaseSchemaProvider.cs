using Squill.Dacpac;

namespace Squill.Provider.MariaDb;

/// <summary>
/// The schema provider for MySQL 9. Served by the MariaDB provider assembly (one provider
/// covers both engines), so it lives here alongside the MariaDb types. Discovered by reflection
/// and recorded in a DACPAC via its <see cref="Squill.Dacpac.DatabaseSchemaProvider.DspName"/>.
/// </summary>
public sealed class MySql9DatabaseSchemaProvider : MySqlDatabaseSchemaProviderBase
{
    /// <summary>The canonical instance, discovered by reflection and cached by the registry.</summary>
    public MySql9DatabaseSchemaProvider()
    {
    }

    /// <summary>A per-build instance carrying the declared target floor.</summary>
    public MySql9DatabaseSchemaProvider(TargetVersion? targetVersion)
        : base(targetVersion)
    {
    }

    public override int MajorVersion => 9;

    /// <summary>Measured on 9: unchanged from MySQL 8.</summary>
    public override string DefaultCollation => "utf8mb4_0900_ai_ci";

    /// <inheritdoc />
    // Measured on 9: unchanged from 8.
    protected override (string Utf8Mb3, string Utf8Mb4) DefaultUnicodeCollations
        => ("utf8mb3_general_ci", "utf8mb4_0900_ai_ci");

    /// <summary>
    /// Measured: MySQL's <c>information_schema.VIEWS</c> has no <c>ALGORITHM</c> column at all,
    /// so a declared algorithm cannot be read back and is left unmodeled with a warning. The
    /// engine still honours it and echoes it from <c>SHOW CREATE VIEW</c>.
    /// </summary>
    public override bool ReportsViewAlgorithm => false;
}

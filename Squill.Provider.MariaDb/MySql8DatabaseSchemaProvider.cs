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
}

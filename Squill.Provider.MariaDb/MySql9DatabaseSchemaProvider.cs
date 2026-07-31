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
}

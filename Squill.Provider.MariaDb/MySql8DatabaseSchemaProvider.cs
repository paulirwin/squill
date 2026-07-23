namespace Squill.Provider.MariaDb;

/// <summary>
/// The schema provider for MySQL 8. Served by the MariaDB provider assembly (one provider
/// covers both engines), so it lives here alongside the MariaDb types. Discovered by reflection
/// and recorded in a DACPAC via its <see cref="Squill.Dacpac.DatabaseSchemaProvider.DspName"/>.
/// </summary>
public sealed class MySql8DatabaseSchemaProvider : MySqlDatabaseSchemaProviderBase
{
    public override int MajorVersion => 8;
}

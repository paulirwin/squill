namespace Squill.Provider.MariaDb;

/// <summary>
/// The schema provider for MariaDB 11. Discovered by reflection and recorded in a DACPAC
/// (via its <see cref="Squill.Dacpac.DatabaseSchemaProvider.DspName"/>) when a project targets
/// this version.
/// </summary>
public sealed class MariaDb11DatabaseSchemaProvider : MariaDbDatabaseSchemaProviderBase
{
    public override int MajorVersion => 11;
}

namespace Squill.Provider.Postgres;

/// <summary>
/// The schema provider for PostgreSQL 15. Discovered by reflection and recorded in a DACPAC
/// (via its <see cref="Squill.Dacpac.DatabaseSchemaProvider.DspName"/>) when a project targets
/// this version.
/// </summary>
public sealed class Postgresql15DatabaseSchemaProvider : PostgresqlDatabaseSchemaProvider
{
    public override int MajorVersion => 15;
}

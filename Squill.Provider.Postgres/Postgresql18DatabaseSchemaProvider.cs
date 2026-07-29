namespace Squill.Provider.Postgres;

/// <summary>
/// The schema provider for PostgreSQL 18. Discovered by reflection and recorded in a DACPAC
/// (via its <see cref="Squill.Dacpac.DatabaseSchemaProvider.DspName"/>) when a project targets
/// this version.
/// </summary>
public sealed class Postgresql18DatabaseSchemaProvider : PostgresqlDatabaseSchemaProvider
{
    public override int MajorVersion => 18;

    // SET EXPRESSION arrived in PostgreSQL 17.
    public override bool SupportsSetExpression => true;
}

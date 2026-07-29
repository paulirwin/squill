namespace Squill.Provider.Postgres;

/// <summary>
/// The schema provider for PostgreSQL 16. Discovered by reflection and recorded in a DACPAC
/// (via its <see cref="Squill.Dacpac.DatabaseSchemaProvider.DspName"/>) when a project targets
/// this version.
/// </summary>
public sealed class Postgresql16DatabaseSchemaProvider : PostgresqlDatabaseSchemaProvider
{
    public override int MajorVersion => 16;

    // SET EXPRESSION arrived in PostgreSQL 17; on this major it is a syntax error, so a
    // changed generation expression is applied by rebuilding the column.
    public override bool SupportsSetExpression => false;
}

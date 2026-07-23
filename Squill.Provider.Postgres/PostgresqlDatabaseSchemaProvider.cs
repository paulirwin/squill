using Squill.Dacpac;

namespace Squill.Provider.Postgres;

/// <summary>
/// Base for the per-major-version PostgreSQL schema providers. Fixes the
/// <see cref="DatabaseSchemaProvider.ProviderName"/> so each concrete version subclass need
/// only declare its <see cref="DatabaseSchemaProvider.MajorVersion"/>. This type is abstract,
/// so it is not itself discovered by <see cref="DatabaseSchemaProviderRegistry"/>; the
/// concrete subclasses remain distinct types whose full names are recorded in DACPACs.
/// </summary>
public abstract class PostgresqlDatabaseSchemaProvider : DatabaseSchemaProvider
{
    public override string ProviderName => "Postgresql";
}

using Squill.Dacpac;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Base for the per-major-version MySQL schema providers. Served by the MariaDB provider
/// assembly (one provider covers both engines). Fixes the
/// <see cref="DatabaseSchemaProvider.ProviderName"/> to <c>MySql</c> so each concrete version
/// subclass need only declare its <see cref="DatabaseSchemaProvider.MajorVersion"/>. Abstract,
/// so it is not itself discovered; the concrete subclasses remain distinct types whose full
/// names are recorded in DACPACs.
/// </summary>
public abstract class MySqlDatabaseSchemaProviderBase : DatabaseSchemaProvider
{
    public override string ProviderName => "MySql";
}

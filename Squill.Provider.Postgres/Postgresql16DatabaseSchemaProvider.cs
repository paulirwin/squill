using Squill.Dacpac;

namespace Squill.Provider.Postgres;

/// <summary>
/// The schema provider for PostgreSQL 16. Discovered by reflection and recorded in a DACPAC
/// (via its <see cref="DatabaseSchemaProvider.DspName"/>) when a project targets this version.
/// </summary>
public sealed class Postgresql16DatabaseSchemaProvider : DatabaseSchemaProvider
{
    public override string ProviderName => "Postgresql";

    public override int MajorVersion => 16;
}

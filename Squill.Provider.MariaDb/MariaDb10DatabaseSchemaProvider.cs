using Squill.Dacpac;

namespace Squill.Provider.MariaDb;

/// <summary>
/// The schema provider for MariaDB 10. Discovered by reflection and recorded in a DACPAC
/// (via its <see cref="DatabaseSchemaProvider.DspName"/>) when a project targets this version.
/// </summary>
public sealed class MariaDb10DatabaseSchemaProvider : DatabaseSchemaProvider
{
    public override string ProviderName => "MariaDb";

    public override int MajorVersion => 10;
}

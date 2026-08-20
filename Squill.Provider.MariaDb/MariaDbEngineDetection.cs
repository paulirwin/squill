using Squill.Dacpac;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Classifies a live server's version banner into the schema provider for the engine actually
/// running (issue #211).
///
/// <para>
/// One provider serves both MariaDB and MySQL, and a few pieces of DDL cannot be written
/// neutrally for the two: index visibility is spelled <c>IGNORED</c> on one and
/// <c>INVISIBLE</c> on the other, and each rejects the other's keyword with a syntax error. So
/// anything that generates DDL against a live connection has to know which engine it reached,
/// and the banner is the only thing that says so.
/// </para>
///
/// <para>
/// Shared rather than duplicated because the extraction side asks the same question, and the
/// two must agree: a build that extracts as MySQL but scripts as MariaDB would compare one
/// dialect against the other.
/// </para>
/// </summary>
public static class MariaDbEngineDetection
{
    /// <summary>
    /// The schema provider for a MySqlConnector server-version string. MariaDB always carries
    /// <c>MariaDB</c> in its banner (e.g. <c>11.4.2-MariaDB</c>); MySQL never does.
    ///
    /// Resolves the server's own major where it is supported, falling back to the latest known
    /// one otherwise: this must still work against a server newer or older than the versions
    /// this build ships providers for, and the capabilities involved do not vary by major
    /// within an engine.
    /// </summary>
    public static MariaDbFamilyDatabaseSchemaProvider FromServerVersion(string serverVersion)
    {
        var providerName = serverVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase)
            ? "MariaDb"
            : "MySql";

        var major = MariaDbDatabase.ParseMajorVersion(serverVersion);

        var schemaProvider = DatabaseSchemaProviderRegistry.All
            .OfType<MariaDbFamilyDatabaseSchemaProvider>()
            .FirstOrDefault(p =>
                string.Equals(p.ProviderName, providerName, StringComparison.OrdinalIgnoreCase)
                && p.MajorVersion == major);

        return schemaProvider
            ?? (MariaDbFamilyDatabaseSchemaProvider)
                DatabaseSchemaProviderRegistry.ResolveLatest(providerName);
    }
}

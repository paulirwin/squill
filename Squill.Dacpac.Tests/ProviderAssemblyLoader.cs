using System.Runtime.CompilerServices;

namespace Squill.Dacpac.Tests;

/// <summary>
/// Forces the provider assemblies to load before any test runs, so the reflection-based
/// <see cref="DatabaseSchemaProviderRegistry"/> can discover their <c>DatabaseSchemaProvider</c>
/// types. .NET loads assemblies lazily and the C# compiler elides project references whose
/// types are never used in code, so a test that only goes through <c>Squill.Dacpac</c> would
/// otherwise never load the providers. Production entry points (the CLI and the MSBuild task)
/// achieve the same by instantiating the concrete providers at startup.
/// </summary>
internal static class ProviderAssemblyLoader
{
    [ModuleInitializer]
    internal static void EnsureProvidersLoaded()
    {
        // Instantiating one type from each provider assembly forces it to load into the
        // AppDomain (a bare typeof reference can be elided and does not reliably trigger a load).
        _ = new Squill.Provider.Postgres.Postgresql16DatabaseSchemaProvider().MajorVersion;
        _ = new Squill.Provider.MariaDb.MariaDb11DatabaseSchemaProvider().MajorVersion;
    }
}

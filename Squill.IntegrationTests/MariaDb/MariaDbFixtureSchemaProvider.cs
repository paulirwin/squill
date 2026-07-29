using Squill.Dacpac;
using Squill.Provider.MariaDb;
using Squill.TestFramework;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// Resolves the schema provider for the engine a dual-engine fixture is actually running.
///
/// This lives here, on the MariaDB side, rather than as a property on
/// <see cref="MariaDbLikeFixture"/>: the fixture's job is to run a container and hand out a
/// connection string, and its <see cref="MariaDbLikeFixture.ProviderName"/> is the
/// engine-neutral fact these tests need. Having the shared test framework return a
/// MariaDB-family-specific type would pull dialect knowledge into infrastructure that has no
/// business knowing about it.
///
/// Every MariaDB-family test class runs its scenarios twice — once against a MariaDB container
/// and once against MySQL — so a test that builds for a fixed engine while connecting to the
/// other is exactly the mismatch issue #147 is about: the model would be canonicalized for the
/// wrong dialect and the round-trip assertion would compare two different things.
/// </summary>
internal static class MariaDbFixtureSchemaProvider
{
    /// <summary>
    /// The fixture engine's schema provider, at its latest supported major. The capabilities
    /// these tests exercise are engine-wide rather than per-major, so the latest is
    /// representative of any supported version of that engine.
    /// </summary>
    public static MariaDbFamilyDatabaseSchemaProvider SchemaProviderOf(
        this MariaDbLikeFixture fixture)
        => (MariaDbFamilyDatabaseSchemaProvider)
            DatabaseSchemaProviderRegistry.ResolveLatest(fixture.ProviderName);
}

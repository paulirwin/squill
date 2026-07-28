using Squill.Provider.MariaDb;
using Squill.TestFramework;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// Maps a dual-engine test fixture to the <see cref="MariaDbEngine"/> a model must be built for
/// when it will be compared against that fixture's server.
///
/// Every MariaDB-family test class runs its scenarios twice — once against a MariaDB container
/// and once against MySQL — so a test that hardcodes one engine while connecting to the other
/// is exactly the mismatch issue #147 is about: the model would be canonicalized for the wrong
/// dialect and the round-trip assertion would compare two different things.
/// </summary>
internal static class MariaDbFixtureEngine
{
    public static MariaDbEngine EngineOf(this MariaDbLikeFixture fixture)
        => string.Equals(fixture.ProviderName, "MySql", StringComparison.OrdinalIgnoreCase)
            ? MariaDbEngine.MySql
            : MariaDbEngine.MariaDb;
}

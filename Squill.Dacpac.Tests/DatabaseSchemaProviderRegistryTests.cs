namespace Squill.Dacpac.Tests;

public class DatabaseSchemaProviderRegistryTests
{
    [Fact]
    public void All_DiscoversProviderTypesAcrossProviderAssemblies()
    {
        var all = DatabaseSchemaProviderRegistry.All;

        // Both provider assemblies contribute types (Postgres and MariaDb/MySQL).
        Assert.Contains(all, p => p is { ProviderName: "Postgresql", MajorVersion: 16 });
        Assert.Contains(all, p => p is { ProviderName: "MariaDb", MajorVersion: 11 });
        Assert.Contains(all, p => p is { ProviderName: "MySql", MajorVersion: 8 });
    }

    [Fact]
    public void ResolveByDspName_ReturnsMatchingProvider()
    {
        var provider = DatabaseSchemaProviderRegistry.ResolveByDspName(
            "Squill.Provider.Postgres.Postgresql16DatabaseSchemaProvider");

        Assert.Equal("Postgresql", provider.ProviderName);
        Assert.Equal(16, provider.MajorVersion);
    }

    [Fact]
    public void ResolveByDspName_UnknownName_Throws()
    {
        Assert.Throws<UnsupportedTargetVersionException>(() =>
            DatabaseSchemaProviderRegistry.ResolveByDspName(
                "Squill.Provider.Postgres.Postgresql99DatabaseSchemaProvider"));
    }

    [Fact]
    public void Resolve_UnsupportedVersion_Throws()
    {
        Assert.Throws<UnsupportedTargetVersionException>(() =>
            DatabaseSchemaProviderRegistry.Resolve("Postgresql", 13));
    }

    /// <summary>
    /// Every discovered provider must expose the <c>(TargetVersion?)</c> constructor the registry
    /// carries a declared point release through. The lookup is by reflection and falls back to the
    /// unversioned instance when it is missing, so an engine that omits it silently gates at its
    /// major's oldest release: a project declaring 10.5.3 would be answered for 10.0.0, and the
    /// omission is invisible at compile time.
    ///
    /// Asserted over the whole discovered set rather than a list of names, so a schema provider
    /// added for a new major fails here rather than shipping with the gap.
    /// </summary>
    [Fact]
    public void EveryProvider_CanCarryADeclaredTargetVersion()
    {
        var missing = DatabaseSchemaProviderRegistry.All
            .Where(p => p.GetType().GetConstructor([typeof(TargetVersion?)]) is null)
            .Select(p => p.GetType().FullName)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "These schema providers have no (TargetVersion?) constructor, so a declared point "
            + "release would be silently dropped: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The same property stated through the public API, for every engine the registry knows.
    /// A declared point release must reach <see cref="DatabaseSchemaProvider.Floor"/>, which is
    /// what the feature gates actually read.
    /// </summary>
    [Theory]
    [InlineData("Postgresql", "16.2.1")]
    [InlineData("MariaDb", "10.5.3")]
    [InlineData("MySql", "8.0.13")]
    public void ADeclaredPointRelease_ReachesTheFloor(string providerName, string declared)
    {
        var provider = DatabaseSchemaProviderRegistry.Resolve(
            providerName, TargetVersion.Parse(declared));

        Assert.Equal(TargetVersion.Parse(declared), provider.Floor);
    }

    /// <summary>
    /// A provider name accepted by a host provider's <c>Matches</c> must also resolve here. The
    /// two run off the same raw name, so a name only one side accepts fails partway through a
    /// build reporting an unsupported target version rather than an unknown provider.
    /// </summary>
    [Theory]
    [InlineData("Postgresql")]
    [InlineData("PostgreSQL")]
    [InlineData("Postgres")]
    public void ResolveLatest_AcceptsEveryPostgresSpelling(string providerName)
    {
        Assert.Equal(
            "Postgresql",
            DatabaseSchemaProviderRegistry.ResolveLatest(providerName).ProviderName);
    }

    [Theory]
    [InlineData("Postgresql", 14)]
    [InlineData("Postgresql", 18)]
    [InlineData("MariaDb", 10)]
    [InlineData("MariaDb", 12)]
    [InlineData("MySql", 9)]
    public void Resolve_SupportedVersion_ReturnsProviderWhoseDspNameIsTheTypeName(
        string providerName, int majorVersion)
    {
        var provider = DatabaseSchemaProviderRegistry.Resolve(providerName, majorVersion);

        Assert.Equal(providerName, provider.ProviderName);
        Assert.Equal(majorVersion, provider.MajorVersion);
        Assert.EndsWith($"{providerName}{majorVersion}DatabaseSchemaProvider", provider.DspName);
        Assert.StartsWith("Squill.Provider.", provider.DspName);
    }

    /// <summary>
    /// A project that declares no target version still gets a schema provider — the engine's
    /// latest supported major. Every build having one is what lets engine capabilities be read
    /// without a null check or a fallback path anywhere downstream.
    /// </summary>
    [Theory]
    [InlineData("Postgresql")]
    [InlineData("MariaDb")]
    [InlineData("MySql")]
    public void ResolveLatest_ReturnsTheHighestSupportedMajor(string providerName)
    {
        var latest = DatabaseSchemaProviderRegistry.ResolveLatest(providerName);

        var expected = DatabaseSchemaProviderRegistry.All
            .Where(p => string.Equals(p.ProviderName, providerName, StringComparison.OrdinalIgnoreCase))
            .Max(p => p.MajorVersion);

        Assert.Equal(providerName, latest.ProviderName);
        Assert.Equal(expected, latest.MajorVersion);
    }

    /// <summary>A null target version means "latest"; a given one is honoured exactly.</summary>
    [Fact]
    public void Resolve_WithOptionalVersion_DefaultsToLatestAndHonoursAnExplicitOne()
    {
        var unconstrained = DatabaseSchemaProviderRegistry.Resolve("MariaDb", (int?)null);
        var pinned = DatabaseSchemaProviderRegistry.Resolve("MariaDb", (int?)10);

        Assert.Equal(
            DatabaseSchemaProviderRegistry.ResolveLatest("MariaDb").MajorVersion,
            unconstrained.MajorVersion);
        Assert.Equal(10, pinned.MajorVersion);
    }

    [Fact]
    public void ResolveLatest_UnknownEngine_Throws()
    {
        Assert.Throws<UnsupportedTargetVersionException>(() =>
            DatabaseSchemaProviderRegistry.ResolveLatest("NotAnEngine"));
    }
}

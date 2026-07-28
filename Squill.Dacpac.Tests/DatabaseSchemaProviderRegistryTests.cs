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

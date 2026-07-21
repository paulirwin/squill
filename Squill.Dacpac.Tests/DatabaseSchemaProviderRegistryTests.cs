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
}

namespace Squill.Dacpac.Tests;

public class DspNameTests
{
    [Fact]
    public void Build_WithVersion_EncodesProviderAndVersion()
    {
        var name = DspName.Build("Postgresql", 16);

        Assert.Equal(
            "Squill.Data.Tools.Schema.Postgresql.Postgresql16DatabaseSchemaProvider", name);
    }

    [Fact]
    public void Build_WithoutVersion_OmitsVersionSegment()
    {
        var name = DspName.Build("MariaDb", null);

        Assert.Equal(
            "Squill.Data.Tools.Schema.MariaDb.MariaDbDatabaseSchemaProvider", name);
    }

    [Fact]
    public void Build_EmptyProvider_Throws()
    {
        Assert.Throws<ArgumentException>(() => DspName.Build("", 16));
    }

    [Fact]
    public void Build_NegativeVersion_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DspName.Build("Postgresql", -1));
    }

    [Theory]
    [InlineData("Squill.Data.Tools.Schema.Postgresql.Postgresql16DatabaseSchemaProvider", "Postgresql", 16)]
    [InlineData("Squill.Data.Tools.Schema.MariaDb.MariaDb11DatabaseSchemaProvider", "MariaDb", 11)]
    [InlineData("Squill.Data.Tools.Schema.MySql.MySql8DatabaseSchemaProvider", "MySql", 8)]
    public void TryParse_WithVersion_RecoversProviderAndVersion(
        string dspName, string expectedProvider, int expectedVersion)
    {
        var ok = DspName.TryParse(dspName, out var provider, out var version);

        Assert.True(ok);
        Assert.Equal(expectedProvider, provider);
        Assert.Equal(expectedVersion, version);
    }

    [Fact]
    public void TryParse_VersionlessName_YieldsNullVersion()
    {
        var ok = DspName.TryParse(
            "Squill.Data.Tools.Schema.Postgresql.PostgresqlDatabaseSchemaProvider",
            out var provider, out var version);

        Assert.True(ok);
        Assert.Equal("Postgresql", provider);
        Assert.Null(version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a dsp name")]
    [InlineData("Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider")]
    public void TryParse_NonSquillName_ReturnsFalse(string? dspName)
    {
        var ok = DspName.TryParse(dspName, out var provider, out var version);

        Assert.False(ok);
        Assert.Equal(string.Empty, provider);
        Assert.Null(version);
    }

    [Theory]
    [InlineData("Postgresql", 16)]
    [InlineData("MariaDb", 11)]
    [InlineData("Postgresql", null)]
    public void BuildThenTryParse_RoundTrips(string provider, int? version)
    {
        var name = DspName.Build(provider, version);

        var ok = DspName.TryParse(name, out var parsedProvider, out var parsedVersion);

        Assert.True(ok);
        Assert.Equal(provider, parsedProvider);
        Assert.Equal(version, parsedVersion);
    }
}

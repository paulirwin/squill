namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Unit tests for parsing the major version out of the driver's server-version string, which
/// backs the deploy-time target-version enforcement (issue #39). MariaDB reports a
/// <c>-MariaDB</c>-suffixed string; MySQL reports a bare dotted version.
/// </summary>
public class MariaDbVersionParseTests
{
    [Theory]
    [InlineData("11.4.2-MariaDB", 11)]
    [InlineData("10.11.6-MariaDB-1:10.11.6+maria~ubu2204", 10)]
    [InlineData("8.0.36", 8)]
    [InlineData("5.7.44-log", 5)]
    [InlineData("11", 11)]
    // MariaDB 10+ prepends the fixed legacy "5.5.5-" replication-version prefix; the real
    // major follows it and must not be read as 5.
    [InlineData("5.5.5-10.11.18-MariaDB", 10)]
    [InlineData("5.5.5-11.4.2-MariaDB", 11)]
    [InlineData("5.5.5-12.0.1-MariaDB", 12)]
    // A genuine MySQL 5.5.x (no MariaDB prefix) is still major 5.
    [InlineData("5.5.62", 5)]
    public void ParseMajorVersion_ReturnsLeadingMajor(string serverVersion, int expected)
    {
        Assert.Equal(expected, MariaDbDatabase.ParseMajorVersion(serverVersion));
    }

    [Theory]
    [InlineData("")]
    [InlineData("MariaDB")]
    [InlineData("-MariaDB")]
    public void ParseMajorVersion_NonNumericLead_Throws(string serverVersion)
    {
        Assert.Throws<InvalidOperationException>(() => MariaDbDatabase.ParseMajorVersion(serverVersion));
    }

    /// <summary>
    /// The minor is load-bearing on these engines (issue #189): much of the MySQL DDL surface
    /// arrived in point releases, so 8.0 and 8.4 are different deploy targets.
    /// </summary>
    [Theory]
    [InlineData("8.0.36", 8, 0, 36)]
    [InlineData("8.4.0", 8, 4, 0)]
    [InlineData("9.1.0", 9, 1, 0)]
    [InlineData("11.4.2-MariaDB", 11, 4, 2)]
    [InlineData("10.11.6-MariaDB-1:10.11.6+maria~ubu2204", 10, 11, 6)]
    [InlineData("5.7.44-log", 5, 7, 44)]
    // The legacy replication prefix must be stripped before the rest is read, or the components
    // would come back as 5.5.5 from the prefix itself.
    [InlineData("5.5.5-10.11.18-MariaDB", 10, 11, 18)]
    [InlineData("5.5.5-11.4.2-MariaDB", 11, 4, 2)]
    // A banner that stops early leaves the omitted components at 0.
    [InlineData("11", 11, 0, 0)]
    [InlineData("8-suffix", 8, 0, 0)]
    [InlineData("8.4", 8, 4, 0)]
    [InlineData("8.4-suffix", 8, 4, 0)]
    public void ParseServerVersion_ReturnsAllComponents(
        string serverVersion, int expectedMajor, int expectedMinor, int expectedPatch)
    {
        var version = MariaDbDatabase.ParseServerVersion(serverVersion);

        Assert.Equal(expectedMajor, version.Major);
        Assert.Equal(expectedMinor, version.Minor);
        Assert.Equal(expectedPatch, version.Patch);
    }

    [Theory]
    [InlineData("")]
    [InlineData("MariaDB")]
    [InlineData("-MariaDB")]
    public void ParseServerVersion_NonNumericLead_Throws(string serverVersion)
    {
        Assert.Throws<InvalidOperationException>(() => MariaDbDatabase.ParseServerVersion(serverVersion));
    }
}

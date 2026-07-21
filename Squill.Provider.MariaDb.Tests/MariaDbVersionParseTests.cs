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
}

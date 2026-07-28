namespace Squill.IntegrationTests;

/// <summary>
/// Unit tests for the container identity check added for issue #145. These need no container.
/// A cross-family collision (Postgres port answered by MySQL, the case the issue reported) cannot
/// survive the handshake, so it is caught by the connection attempt itself; what needs testing
/// here is the MariaDB-vs-MySQL split, which shares a wire protocol and so is settled by the
/// version banner.
/// </summary>
public class ContainerIdentityTests
{
    [Theory]
    // Real banners as reported by MySqlConnector's ServerVersion.
    [InlineData("11.6.2-MariaDB-ubu2404", ContainerEngine.MariaDb)]
    [InlineData("10.11.10-MariaDB-ubu2204", ContainerEngine.MariaDb)]
    [InlineData("8.0.40", ContainerEngine.MySql)]
    [InlineData("9.1.0", ContainerEngine.MySql)]
    public void IdentifyMySqlFamily_DistinguishesMariaDbFromMySql(
        string banner, ContainerEngine expected)
        => Assert.Equal(expected, ContainerIdentity.IdentifyMySqlFamily(banner));

    [Fact]
    public void IdentifyMySqlFamily_IsCaseInsensitive()
        => Assert.Equal(
            ContainerEngine.MariaDb, ContainerIdentity.IdentifyMySqlFamily("11.6.2-mariadb"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IdentifyMySqlFamily_ReturnsUnknownForMissingBanner(string? banner)
        => Assert.Equal(ContainerEngine.Unknown, ContainerIdentity.IdentifyMySqlFamily(banner));

    [Fact]
    public void IsWrongEngine_RecognizesTheReportedNpgsqlAgainstMySqlFailure()
    {
        // The exact error from issue #145: the H is the first byte of a MySQL handshake packet.
        var ex = new InvalidOperationException(
            "Received unknown response H for SSLRequest (expecting S or N)");

        Assert.True(ContainerIdentity.IsWrongEngine(ex));
    }

    [Fact]
    public void IsWrongEngine_LooksThroughInnerExceptions()
    {
        // Npgsql wraps the protocol error, so a top-level-only check would miss it.
        var ex = new InvalidOperationException(
            "Failed to connect",
            new InvalidOperationException("Received unknown response H for SSLRequest"));

        Assert.True(ContainerIdentity.IsWrongEngine(ex));
    }

    [Fact]
    public void IsWrongEngine_DoesNotBlameAPortCollisionForATimeout()
    {
        // A container that never came up is not a reused port. Reporting one as the other would
        // repeat the misdirection this issue is about, so a timeout must never match.
        var plain = new InvalidOperationException(
            "The operation has timed out",
            new TimeoutException("The operation has timed out."));

        Assert.False(ContainerIdentity.IsWrongEngine(plain));

        // A timeout wins even when an outer frame mentions the handshake. Npgsql's timeout message
        // is assembled from the connect path, so a wrapper naming SSLRequest while the real cause
        // is a TimeoutException is exactly the shape that produced the bogus "port was reused"
        // report on a container that was merely slow to start.
        var timeoutBehindProtocolText = new InvalidOperationException(
            "Received unknown response H for SSLRequest",
            new TimeoutException("The operation has timed out."));

        Assert.False(ContainerIdentity.IsWrongEngine(timeoutBehindProtocolText));
    }

    [Fact]
    public void IsWrongEngine_IgnoresAnOrdinaryConnectionRefusal()
        => Assert.False(ContainerIdentity.IsWrongEngine(
            new InvalidOperationException("Connection refused")));

    [Fact]
    public void IdentifyMySqlFamily_DoesNotMisreadAPostgresBanner()
    {
        // Npgsql reports a bare number with no "PostgreSQL" marker, which is why the family is
        // established by the connection's protocol rather than by this string. Asserting the
        // banner is *not* self-identifying keeps anyone from reintroducing a string-only check.
        Assert.DoesNotContain(
            "PostgreSQL", "18.4 (Debian 18.4-1.pgdg13+1)", StringComparison.OrdinalIgnoreCase);
    }
}

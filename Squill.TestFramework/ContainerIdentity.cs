using System.Data.Common;

namespace Squill.TestFramework;

/// <summary>
/// Confirms that a freshly started test container's host port really is serving the engine the
/// fixture asked for (issue #145).
/// </summary>
/// <remarks>
/// <para>
/// Nothing in this repository pins a host port — every fixture lets Testcontainers assign an
/// ephemeral one and reads it back only via <c>GetConnectionString()</c> after the container is
/// started. The flakiness comes from churn instead: a full integration run cycles roughly seventy
/// containers (about forty per-class Postgres containers plus about thirty MariaDB/MySQL
/// fixtures) with no concurrency cap, so the OS can hand a just-released ephemeral port to a new
/// container of a *different* engine while a client still holds the older connection string. The
/// Postgres client then performs its SSL handshake against a MySQL server and reports
/// <c>Received unknown response H for SSLRequest</c> — an error that names neither the real cause
/// nor the test that actually misbehaved.
/// </para>
/// <para>
/// Capping parallelism (see <c>xunit.runner.json</c>) makes that reuse far less likely, but "far
/// less likely" is not a guarantee, and a race that resurfaces once a quarter is worse than one
/// that resurfaces weekly: it is the failure reviewers learn to shrug at. So each fixture also
/// probes its own port once, at startup. A mismatch then points at the port collision directly
/// rather than surfacing later as an unrelated-looking schema assertion in whichever test happened
/// to draw the bad port.
/// </para>
/// <para>
/// The check leans on the wire protocol rather than on the server's version string. Each client
/// speaks exactly one protocol, so a cross-family collision — the reported case, Npgsql meeting a
/// MySQL server — cannot finish the handshake and is caught by the connect attempt itself. The
/// banner is consulted only for the MariaDB-vs-MySQL split, which shares a protocol. Testing the
/// banner for the family instead would be wrong: Npgsql reports a bare <c>18.4 (Debian …)</c> with
/// no <c>PostgreSQL</c> in it, so a string check would reject every healthy Postgres container.
/// </para>
/// </remarks>
public static class ContainerIdentity
{
    /// <summary>
    /// Connects to the container and asserts the server behind it is the expected engine, throwing
    /// <see cref="InvalidOperationException"/> if it is not. Retries a container that has not
    /// finished starting.
    /// </summary>
    /// <param name="connectionFactory">
    /// Creates a new closed connection to the container. Called once per attempt — a failed open
    /// can leave a connection unusable — and the connection is disposed here.
    /// </param>
    /// <param name="expectedEngine">
    /// The engine the fixture started. The connection's own protocol establishes the family (a
    /// cross-family collision cannot complete the handshake at all); the version banner is
    /// consulted only to tell MariaDB from MySQL.
    /// </param>
    /// <param name="cancellationToken">A cancellation token.</param>
    public static async Task VerifyAsync(
        Func<DbConnection> connectionFactory,
        ContainerEngine expectedEngine,
        CancellationToken cancellationToken = default)
    {
        await using var connection =
            await OpenWithRetryAsync(connectionFactory, expectedEngine, cancellationToken);

        // Reaching this point already proves the engine family: each client speaks exactly one
        // wire protocol, so a successful handshake by an Npgsql connection means a Postgres server
        // answered, and a MySqlConnector one means a MariaDB/MySQL server did. A cross-family
        // collision therefore fails in OpenAsync above, which is the case issue #145 reported.
        // Only the MariaDB-vs-MySQL split remains, and that the banner can settle.
        if (expectedEngine is not (ContainerEngine.MariaDb or ContainerEngine.MySql))
        {
            return;
        }

        var actual = IdentifyMySqlFamily(connection.ServerVersion);

        if (actual != expectedEngine)
        {
            throw new InvalidOperationException(
                $"The {expectedEngine} test container's host port is being served by {actual} "
                + $"(server version '{connection.ServerVersion}'). An ephemeral host port was "
                + "reused by another engine's container — see issue #145.");
        }
    }

    /// <summary>
    /// Opens the connection, retrying a container that is not yet accepting connections.
    /// </summary>
    /// <remarks>
    /// Testcontainers' wait strategy probes the server inside the container, which can report ready
    /// a moment before the host-side port forward is actually usable — and under the parallel load
    /// this check exists to police, that gap widens. This probe is the first thing to touch the
    /// port, so without a retry it would convert an ordinary slow start into a failure, trading one
    /// source of flakiness for another.
    /// </remarks>
    private static async Task<DbConnection> OpenWithRetryAsync(
        Func<DbConnection> connectionFactory,
        ContainerEngine expectedEngine,
        CancellationToken cancellationToken)
    {
        const int attempts = 5;

        for (var attempt = 1; ; attempt++)
        {
            // A fresh connection per attempt: a failed open can leave the object unusable, so
            // retrying the same instance would not reliably reconnect.
            var connection = connectionFactory();

            try
            {
                await connection.OpenAsync(cancellationToken);
                return connection;
            }
            catch (Exception ex) when (attempt < attempts && !IsWrongEngine(ex))
            {
                await connection.DisposeAsync();

                // Back off and try again: the container is most likely still coming up.
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
            catch (Exception ex)
            {
                await connection.DisposeAsync();

                // Only blame a port collision when the failure actually looks like one. A timeout
                // means nothing answered, which is a container that never came up — saying "the
                // port was reused" there would be the same misdirection issue #145 is about.
                throw new InvalidOperationException(
                    IsWrongEngine(ex)
                        ? $"The {expectedEngine} test container's host port answered with another "
                          + "engine's protocol. An ephemeral host port was reused by a different "
                          + "container — see issue #145."
                        : $"Could not connect to the {expectedEngine} test container after "
                          + $"{attempts} attempts; it did not become ready in time.",
                    ex);
            }
        }
    }

    /// <summary>
    /// Whether an exception is the signature of a client meeting a server of a different family —
    /// the wrong-protocol reply issue #145 reported, as opposed to nothing answering at all.
    /// </summary>
    public static bool IsWrongEngine(Exception ex)
    {
        // A timeout anywhere in the chain settles it: nothing answered, so nothing can have
        // answered with the wrong protocol. This is checked across the whole chain before the
        // message scan because Npgsql builds its timeout message from the connect path, so an
        // outer frame can name the handshake while the real cause is a slow container.
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
            {
                return false;
            }
        }

        for (var current = ex; current is not null; current = current.InnerException)
        {
            // Npgsql's "Received unknown response H for SSLRequest (expecting S or N)" — the H is
            // the first byte of a MySQL handshake packet. MySqlConnector reports the mirror case as
            // a malformed/unexpected packet.
            if (current.Message.Contains("SSLRequest", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("unknown response", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("Packet received out-of-order", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("not a valid MySQL", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Distinguishes MariaDB from MySQL by version banner. This is only meaningful once a
    /// <c>MySqlConnection</c> has connected — the banner cannot identify the engine family on its
    /// own, because Npgsql reports a bare number (<c>18.4 (Debian …)</c>) with no <c>PostgreSQL</c>
    /// marker, which would make any "is this Postgres?" test on the string alone misfire. The
    /// family is established by which client completed the handshake; only the MariaDB/MySQL split
    /// needs the banner, and MariaDB embeds its own name precisely so it can be told apart from
    /// the MySQL it otherwise mimics.
    /// </summary>
    public static ContainerEngine IdentifyMySqlFamily(string? serverVersion)
    {
        if (string.IsNullOrWhiteSpace(serverVersion))
        {
            return ContainerEngine.Unknown;
        }

        return serverVersion.Contains("MariaDB", StringComparison.OrdinalIgnoreCase)
            ? ContainerEngine.MariaDb
            : ContainerEngine.MySql;
    }
}

/// <summary>The database engine a test container is expected to be running.</summary>
public enum ContainerEngine
{
    /// <summary>The server did not report a usable version banner.</summary>
    Unknown,

    /// <summary>PostgreSQL.</summary>
    Postgres,

    /// <summary>MariaDB.</summary>
    MariaDb,

    /// <summary>MySQL.</summary>
    MySql,
}

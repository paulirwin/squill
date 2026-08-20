using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using MySqlConnector;
using Squill.Core;
using Squill.Dacpac;

namespace Squill.Provider.MariaDb;

/// <summary>
/// An <see cref="IDatabase"/> over a MariaDB (or MySQL) database, using the MySqlConnector
/// ADO.NET driver. A MariaDB "database" is the unit a schema lives in (there is no separate
/// schema namespace as in Postgres), so <see cref="Name"/> is the database this instance
/// targets, and connecting rebinds the connection string's <c>Database</c> to it.
/// </summary>
public class MariaDbDatabase : IDatabase
{
    private readonly string _connectionString;

    private MySqlConnection? _connection;
    private MariaDbScriptGenerator? _scriptGenerator;

    public MariaDbDatabase(string connectionString, string databaseName)
    {
        _connectionString = connectionString;
        Name = databaseName;
    }

    public string Name { get; }

    [MemberNotNull(nameof(_connection))]
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is { State: ConnectionState.Open })
        {
            return;
        }

        var builder = new MySqlConnectionStringBuilder(_connectionString)
        {
            Database = Name
        };

        _connection = new MySqlConnection(builder.ConnectionString);
        await _connection.OpenAsync(cancellationToken);
    }

    /// <summary>
    /// The version of the connected server (e.g. <c>11.4.2</c> for MariaDB, <c>8.0.36</c> for
    /// MySQL), used to enforce the DACPAC's recorded target version at deploy time. Parsed from
    /// the driver's server-version string, whose MariaDB form carries a <c>-MariaDB</c> suffix
    /// (e.g. <c>11.4.2-MariaDB</c>) and whose MySQL form does not (e.g. <c>8.0.36</c>).
    ///
    /// <para>
    /// The components below the major are load-bearing on these engines: much of the DDL surface
    /// arrived in point releases, so 8.0 and 8.4 are genuinely different targets, as are 8.0.3
    /// and 8.0.13.
    /// </para>
    /// </summary>
    public TargetVersion GetServerVersion()
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Connect to the database before running a script.");
        }

        return ParseServerVersion(_connection.ServerVersion);
    }

    // MariaDB 10.0+ prepends this fixed "replication version" to its reported server version
    // (e.g. "5.5.5-10.11.18-MariaDB") so legacy MySQL clients don't reject it. The real version
    // follows the prefix, so it must be stripped before parsing the major.
    private const string MariaDbLegacyVersionPrefix = "5.5.5-";

    /// <summary>
    /// Extracts the major version from a MySqlConnector server-version string. See
    /// <see cref="ParseServerVersion"/> for the forms handled.
    /// </summary>
    public static int ParseMajorVersion(string serverVersion)
        => ParseServerVersion(serverVersion).Major;

    /// <summary>
    /// Extracts the version from a MySqlConnector server-version string. MySQL reports a bare
    /// dotted version (e.g. <c>8.0.36</c>); MariaDB reports a <c>-MariaDB</c>-suffixed version,
    /// and MariaDB 10+ additionally prepends the fixed legacy prefix <c>5.5.5-</c>
    /// (e.g. <c>5.5.5-10.11.18-MariaDB</c>), which is stripped first. Each dot-separated leading
    /// run of digits is one component; parsing stops at the first non-digit, which is where the
    /// engine's suffix begins.
    ///
    /// <para>
    /// The patch is read, not discarded: these engines introduce DDL in patch releases, so a
    /// target of <c>8.0.13</c> has to be comparable against an <c>8.0.3</c> server. A component
    /// the banner omits is <c>0</c>, matching the floor rule for an unstated component.
    /// </para>
    /// </summary>
    public static TargetVersion ParseServerVersion(string serverVersion)
    {
        var remaining = serverVersion.StartsWith(MariaDbLegacyVersionPrefix, StringComparison.Ordinal)
            ? serverVersion.AsSpan(MariaDbLegacyVersionPrefix.Length)
            : serverVersion.AsSpan();

        var components = new int[3];

        for (var i = 0; i < components.Length; i++)
        {
            var value = ReadLeadingNumber(remaining, out var consumed);

            if (value is null)
            {
                // The major must be present; anything past it is optional, since a banner may
                // stop early or run into its engine suffix.
                if (i == 0)
                {
                    throw new InvalidOperationException(
                        "Could not parse a major version from the server version string "
                        + $"'{serverVersion}'.");
                }

                break;
            }

            components[i] = value.Value;
            remaining = remaining[consumed..];

            // Anything other than a '.' (a '-MariaDB' suffix, or the end of the string) means
            // the banner has no further components to give.
            if (remaining.IsEmpty || remaining[0] != '.')
            {
                break;
            }

            remaining = remaining[1..];
        }

        return new TargetVersion(components[0], components[1], components[2]);
    }

    /// <summary>
    /// Reads the leading run of ASCII digits from <paramref name="text"/>, reporting how many
    /// characters it spanned. Returns <c>null</c> when there is no leading digit.
    /// </summary>
    private static int? ReadLeadingNumber(ReadOnlySpan<char> text, out int consumed)
    {
        consumed = 0;

        while (consumed < text.Length && char.IsAsciiDigit(text[consumed]))
        {
            consumed++;
        }

        if (consumed == 0
            || !int.TryParse(
                text[..consumed],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            return null;
        }

        return value;
    }

    public async Task RunScriptAsync(string sql,
        IReadOnlyList<IDatabaseParameter>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Connect to the database before running a script.");
        }

        await using var cmd = PrepareCommand(_connection, sql, parameters);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DbDataReader> RunScriptReaderAsync(string sql,
        IReadOnlyList<IDatabaseParameter>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Connect to the database before running a script.");
        }

        await using var cmd = PrepareCommand(_connection, sql, parameters);

        return await cmd.ExecuteReaderAsync(cancellationToken);
    }

    public async Task DropAsync(CancellationToken cancellationToken = default)
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }

        await using var conn = new MySqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE `{Name}`;";

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// The script generator for the engine actually on the other end of this connection.
    ///
    /// Built from the live server's version banner rather than fixed at construction, because
    /// some DDL cannot be written engine-neutrally: MariaDB and MySQL spell index visibility
    /// with keywords the other rejects as a syntax error (issue #211), so a generator that did
    /// not know which engine it was targeting would emit DDL that fails to parse.
    /// </summary>
    private MariaDbScriptGenerator ScriptGenerator
    {
        get
        {
            if (_connection is null)
            {
                throw new InvalidOperationException(
                    "Connect to the database before generating a script.");
            }

            return _scriptGenerator ??= new MariaDbScriptGenerator(
                MariaDbEngineDetection.FromServerVersion(_connection.ServerVersion));
        }
    }

    public async Task PublishAsync(SchemaComparison comparison, CancellationToken cancellationToken = default)
    {
        foreach (var delta in comparison.Deltas)
        {
            var sql = ScriptGenerator.GenerateScriptForDelta(delta);
            await RunScriptAsync(sql, cancellationToken: cancellationToken);
        }
    }

    private static MySqlCommand PrepareCommand(MySqlConnection connection, string sql,
        IReadOnlyList<IDatabaseParameter>? parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;

        if (parameters != null)
        {
            foreach (var parameter in parameters)
            {
                cmd.Parameters.Add(new MySqlParameter(parameter.ParameterName, parameter.ParameterValue));
            }
        }

        return cmd;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _connection?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }
    }
}

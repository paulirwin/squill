using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using MySqlConnector;
using Squill.Core;

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
    private readonly MariaDbScriptGenerator _scriptGenerator = new();

    private MySqlConnection? _connection;

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
    /// The major version of the connected server (e.g. <c>11</c> for MariaDB, <c>8</c> for
    /// MySQL), used to enforce the DACPAC's recorded target version at deploy time. Parsed from
    /// the driver's server-version string, whose MariaDB form carries a <c>-MariaDB</c> suffix
    /// (e.g. <c>11.4.2-MariaDB</c>) and whose MySQL form does not (e.g. <c>8.0.36</c>).
    /// </summary>
    public int GetServerMajorVersion()
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Connect to the database before running a script.");
        }

        return ParseMajorVersion(_connection.ServerVersion);
    }

    /// <summary>
    /// Extracts the major version from a MySqlConnector server-version string. The string
    /// starts with a dotted numeric version, optionally followed by a suffix like
    /// <c>-MariaDB</c> (MariaDB) or nothing (MySQL); the leading run of digits is the major.
    /// </summary>
    public static int ParseMajorVersion(string serverVersion)
    {
        var digits = 0;
        var length = 0;

        foreach (var c in serverVersion)
        {
            if (!char.IsAsciiDigit(c))
            {
                break;
            }

            length++;
        }

        if (length == 0
            || !int.TryParse(
                serverVersion.AsSpan(0, length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out digits))
        {
            throw new InvalidOperationException(
                $"Could not parse a major version from the server version string '{serverVersion}'.");
        }

        return digits;
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

    public async Task PublishAsync(SchemaComparison comparison, CancellationToken cancellationToken = default)
    {
        foreach (var delta in comparison.Deltas)
        {
            var sql = _scriptGenerator.GenerateScriptForDelta(delta);
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

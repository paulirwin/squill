using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Npgsql;
using Squill.Core;
using Squill.Dacpac;

namespace Squill.Provider.Postgres;

public class PostgresDatabase : IDatabase
{
    private readonly string _connectionString;
    private readonly PostgresScriptGenerator _scriptGenerator = new();

    private NpgsqlConnection? _connection;

    public PostgresDatabase(string connectionString, string databaseName)
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
            // TODO.PI: handle connecting state?
            return;
        }
        
        var builder = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Database = Name
        };

        _connection = new NpgsqlConnection(builder.ConnectionString);
        await _connection.OpenAsync(cancellationToken);
    }

    /// <summary>
    /// The version of the connected PostgreSQL server (e.g. <c>16.2</c>), used to enforce the
    /// DACPAC's recorded target version at deploy time. Npgsql exposes the server version as a
    /// parsed <see cref="Version"/> once connected, so no query is needed.
    ///
    /// <para>
    /// PostgreSQL's own feature boundaries fall on majors, so the components below it are carried
    /// for a uniform comparison rather than because anything here gates on them. A
    /// <see cref="Version"/> leaves an unstated component at <c>-1</c>, which would compare below
    /// <c>.0</c> and wrongly fail a deploy, so both are floored at zero.
    /// </para>
    /// </summary>
    public TargetVersion GetServerVersion()
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Thou shalt connect first!");
        }

        var version = _connection.PostgreSqlVersion;

        return new TargetVersion(
            version.Major,
            Math.Max(version.Minor, 0),
            Math.Max(version.Build, 0));
    }

    public async Task RunScriptAsync(string sql,
        IReadOnlyList<IDatabaseParameter>? parameters = null,
        CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Thou shalt connect first!");
        }

        await using var cmd = PrepareCommand(_connection, sql, parameters);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DbDataReader> RunScriptReaderAsync(string sql, IReadOnlyList<IDatabaseParameter>? parameters = null, CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Thou shalt connect first!");
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

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        await using var cmd = conn.CreateCommand();
        // HACK.PI: WITH (FORCE) only available in pgsql 13 and later
        cmd.CommandText = $"DROP DATABASE {Name} WITH (FORCE);";

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

    private static NpgsqlCommand PrepareCommand(NpgsqlConnection connection, string sql, IReadOnlyList<IDatabaseParameter>? parameters)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = sql;

        if (parameters != null)
        {
            foreach (var parameter in parameters)
            {
                cmd.Parameters.Add(new NpgsqlParameter(parameter.ParameterName, parameter.ParameterValue));
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
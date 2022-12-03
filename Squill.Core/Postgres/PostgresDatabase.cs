using System.Data;
using System.Diagnostics.CodeAnalysis;
using Npgsql;

namespace Squill.Core.Postgres;

public class PostgresDatabase : IDatabase
{
    private readonly string _connectionString;
    private readonly string _databaseName;

    private NpgsqlConnection? _connection;

    public PostgresDatabase(string connectionString, string databaseName)
    {
        _connectionString = connectionString;
        _databaseName = databaseName;
    }

    [MemberNotNull(nameof(_connection))]
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var builder = new NpgsqlConnectionStringBuilder(_connectionString)
        {
            Database = _databaseName
        };

        _connection = new NpgsqlConnection(builder.ConnectionString);
        await _connection.OpenAsync(cancellationToken);
    }

    public async Task RunScriptAsync(string sql, CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            throw new InvalidOperationException("Thou shalt connect first!");
        }

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Model> ExtractModelAsync(CancellationToken cancellationToken = default)
    {
        var model = new Model();

        if (_connection == null)
        {
            await ConnectAsync(cancellationToken);
        }

        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT * FROM information_schema.tables;";

        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString("table_schema");

                if (schema is "pg_catalog" or "information_schema")
                {
                    continue;
                }

                var catalog = reader.GetString("table_catalog");
                var name = reader.GetString("table_name");

                var element = new Element(PostgresElementTypes.SqlTable)
                {
                    Name = name,
                    Properties =
                    {
                        new Property(PostgresPropertyNames.Catalog, catalog),
                        new Property(PostgresPropertyNames.Schema, schema),
                    }
                };

                model.Elements.Add(element);
            }
        }

        foreach (var element in model.Elements)
        {
            var catalog = element.GetRequiredProperty<string>(PostgresPropertyNames.Catalog);
            var schema = element.GetRequiredProperty<string>(PostgresPropertyNames.Schema);
            
            await ExtractColumnsAsync(catalog, schema, element, cancellationToken);
        }

        return model;
    }

    private async Task ExtractColumnsAsync(string catalog, string schema, Element table,
        CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return;
        }

        await using var cmd =
            new NpgsqlCommand(
                "SELECT * FROM information_schema.columns " +
                "WHERE table_catalog = @catalog " +
                "AND table_schema = @schema " +
                "AND table_name = @name;",
                _connection)
            {
                Parameters =
                {
                    new NpgsqlParameter<string>("@catalog", catalog),
                    new NpgsqlParameter<string>("@schema", schema),
                    new NpgsqlParameter<string>("@name", table.Name!),
                }
            };

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var columns = new Relationship(PostgresRelationshipNames.Columns);
        table.Relationships.Add(columns);

        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString("column_name");
            var nullable = reader.GetString("is_nullable") == "YES";
            var dataType = reader.GetString("data_type");
            var maxLength = reader.GetFieldValue<int?>("character_maximum_length");

            var typeElement = new Element(PostgresElementTypes.SqlTypeSpecifier)
            {
                Relationships =
                {
                    new Relationship(PostgresRelationshipNames.Type)
                    {
                        Entries =
                        {
                            // TODO: support custom types
                            new Reference(dataType)
                            {
                                ExternalSource = "BuiltIns"
                            }
                        }
                    }
                }
            };

            if (maxLength.HasValue)
            {
                typeElement.Properties.Add(new Property(PostgresPropertyNames.Length, maxLength.Value));
            }

            var column = new Element(PostgresElementTypes.SqlSimpleColumn)
            {
                Name = name,
                Relationships =
                {
                    new Relationship(PostgresRelationshipNames.TypeSpecifier)
                    {
                        Entries =
                        {
                            typeElement,
                        }
                    }
                }
            };

            if (!nullable)
            {
                column.Properties.Add(new Property(PostgresPropertyNames.IsNullable, false));
            }

            columns.Entries.Add(column);
        }
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
        cmd.CommandText = $"DROP DATABASE {_databaseName} WITH (FORCE);";

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }
    }
}
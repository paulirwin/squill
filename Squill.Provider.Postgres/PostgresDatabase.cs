using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Npgsql;
using Squill.Core;

namespace Squill.Provider.Postgres;

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

        var tables = new List<Element>();

        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString("table_schema");

                if (schema is "pg_catalog" or "information_schema")
                {
                    continue;
                }

                var name = reader.GetString("table_name");

                var element = new Element(PostgresElementTypes.SqlTable)
                {
                    Name = name,
                    Relationships =
                    {
                        new Relationship(PostgresRelationshipNames.Schema)
                        {
                            new Reference(schema)
                            {
                                ExternalSource = "BuiltIns"
                            }
                        }
                    }
                };

                model.Elements.Add(element);
                tables.Add(element);
            }
        }

        foreach (var table in tables)
        {
            await ExtractColumnsAsync(table, cancellationToken);
            await ExtractPrimaryKeyAsync(model, table, cancellationToken);
        }

        return model;
    }

    private async Task ExtractPrimaryKeyAsync(Model model, Element table, CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return;
        }

        var schemaRelationship = table.Relationships.Single(i => i.Name == PostgresRelationshipNames.Schema);
        // HACK.PI: assume built-in public schema for now
        var schema = schemaRelationship.Entries.OfType<Reference>().First().Name;

        await using var cmd =
            new NpgsqlCommand(
                "SELECT * FROM information_schema.table_constraints " +
                "WHERE table_catalog = @catalog " +
                "AND table_schema = @schema " +
                "AND table_name = @name " +
                "AND constraint_type = 'PRIMARY KEY';",
                _connection)
            {
                Parameters =
                {
                    new NpgsqlParameter<string>("@catalog", _databaseName),
                    new NpgsqlParameter<string>("@schema", schema),
                    new NpgsqlParameter<string>("@name", table.Name!),
                }
            };

        string name, constraintSchema;
        
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return; // no PK
            }

            name = reader.GetString("constraint_name");
            constraintSchema = reader.GetString("constraint_schema");
        }

        // TODO.PI: make this code less procedural
        var constraint = new Element(PostgresElementTypes.SqlPrimaryKeyConstraint)
        {
            Name = name,
            Relationships =
            {
                new Relationship(PostgresRelationshipNames.DefiningTable)
                {
                    new Reference(table.Name!)
                }
            }
        };
        model.Elements.Add(constraint);

        var columnSpec = new Relationship(PostgresRelationshipNames.ColumnSpecifications);
        constraint.Relationships.Add(columnSpec);

        var indexedColumns = new Element(PostgresElementTypes.SqlIndexedColumnSpecification);
        columnSpec.Entries.Add(indexedColumns);

        await ExtractPrimaryKeyColumnsAsync(constraintSchema, name, table.Name!, indexedColumns, cancellationToken);
    }

    private async Task ExtractPrimaryKeyColumnsAsync(string constraintSchema, 
        string constraintName, 
        string tableName,
        Element indexedColumns,
        CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return;
        }

        await using var cmd =
            new NpgsqlCommand(
                "SELECT * FROM information_schema.constraint_column_usage " +
                "WHERE constraint_schema = @schema " +
                "AND constraint_name = @name;",
                _connection)
            {
                Parameters =
                {
                    new NpgsqlParameter<string>("@schema", constraintSchema),
                    new NpgsqlParameter<string>("@name", constraintName),
                }
            };

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

        var columns = new List<string>();
        
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString("column_name"));
        }

        if (columns.Count == 1)
        {
            indexedColumns.Relationships.Add(new Relationship(PostgresRelationshipNames.Column)
            {
                new Reference($"{tableName}.{columns[0]}")
            });
            return;
        }

        throw new NotImplementedException("Handle zero or multiple PK columns");
    }

    private async Task ExtractColumnsAsync(Element table,
        CancellationToken cancellationToken = default)
    {
        if (_connection == null)
        {
            return;
        }

        var schemaRelationship = table.Relationships.Single(i => i.Name == PostgresRelationshipNames.Schema);
        // HACK.PI: assume built-in public schema for now
        var schema = schemaRelationship.Entries.OfType<Reference>().First().Name;

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
                    new NpgsqlParameter<string>("@catalog", _databaseName),
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

    public async Task PublishAsync(SchemaComparison comparison, CancellationToken cancellationToken = default)
    {
        foreach (var delta in comparison.Deltas)
        {
            var sql = GenerateScriptForDelta(delta);
            await RunScriptAsync(sql, cancellationToken);
        }
    }

    private string GenerateScriptForDelta(SchemaDelta delta)
    {
        if (delta is CreateDelta createDelta)
        {
            return GenerateCreateScript(createDelta);
        }

        throw new NotImplementedException();
    }

    private string GenerateCreateScript(CreateDelta createDelta)
    {
        if (createDelta.Element.Type == PostgresElementTypes.SqlTable)
        {
            return GenerateCreateTableScript(createDelta.Element, createDelta.DependentElements);
        }

        throw new NotImplementedException();
    }

    private string GenerateCreateTableScript(Element table, IList<Element> dependentElements)
    {
        if (table.Name is not string tableName)
        {
            throw new ArgumentException("Tables must have names");
        }
        
        var sb = new StringBuilder();

        sb.Append("CREATE TABLE ").Append(tableName).AppendLine("");
        sb.AppendLine("(");

        var columnText = new List<string>();

        var pk = dependentElements.SingleOrDefault(i => i.Type == PostgresElementTypes.SqlPrimaryKeyConstraint);

        var pkColumns = pk == null ? new List<string>() : GetPrimaryKeyColumns(pk);

        foreach (var columnRelationship in table.Relationships.Where(i => i.Name == PostgresRelationshipNames.Columns))
        {
            foreach (var column in columnRelationship.Entries.OfType<Element>().Where(i => i.Type == PostgresElementTypes.SqlSimpleColumn))
            {
                if (column.Name is not string columnName)
                {
                    throw new InvalidOperationException("Missing column name");
                }

                var columnType = GetTypeStringForColumn(column);
                
                var text = $"{columnName} {columnType}";

                if (pkColumns.Count == 1 && pkColumns[0].Equals($"{tableName}.{columnName}"))
                {
                    // TODO: support named PK constraints
                    text += " PRIMARY KEY";
                }
                else
                {
                    var nullable = column.GetProperty<bool?>(PostgresPropertyNames.IsNullable);

                    text += nullable == false ? " NOT NULL" : " NULL";
                }
                
                columnText.Add(text);
            }
        }

        sb.Append("    ").AppendLine(string.Join($",{Environment.NewLine}    ", columnText));
        
        sb.AppendLine(");");
        
        return sb.ToString();
    }

    private static IList<string> GetPrimaryKeyColumns(Element pkConstraint)
    {
        var columnSpec = pkConstraint.GetRelationship(PostgresRelationshipNames.ColumnSpecifications);

        if (columnSpec == null)
        {
            return new List<string>();
        }

        var indexedColumns = columnSpec.GetElement(PostgresElementTypes.SqlIndexedColumnSpecification);

        if (indexedColumns == null)
        {
            throw new InvalidOperationException("ColumnSpecifications relationship does not contain a SqlIndexedColumnSpecification element");
        }

        var column = indexedColumns
            .GetRelationship(PostgresRelationshipNames.Column)
            ?.Entries
            .OfType<Reference>()
            .SingleOrDefault();

        if (column == null)
        {
            throw new NotImplementedException("Support multiple columns in PK");
        }

        return new List<string> { column.Name };
    }

    private string GetTypeStringForColumn(Element column)
    {
        // HACK.PI: assume there's a type specifier and built-in type reference
        var typeSpecifier = column.Relationships.Single(i => i.Name == PostgresRelationshipNames.TypeSpecifier);
        
        var typeElement = typeSpecifier.Entries
            .OfType<Element>()
            .Single(i => i.Type == PostgresElementTypes.SqlTypeSpecifier);

        var type = typeElement.Relationships.Single(i => i.Name == PostgresRelationshipNames.Type);

        var typeReference = type.Entries
            .OfType<Reference>()
            .Single();

        var maxLength = typeElement.GetProperty<int?>(PostgresPropertyNames.Length);

        return typeReference.Name.ToLower() switch
        {
            "varchar" or "nvarchar" => $"{typeReference.Name}({(maxLength != null ? maxLength : "MAX")})",
            "character varying" => $"varchar({(maxLength != null ? maxLength : "MAX")})",
            _ => typeReference.Name,
        };
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
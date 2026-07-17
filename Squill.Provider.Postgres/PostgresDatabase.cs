using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Npgsql;
using Squill.Core;

namespace Squill.Provider.Postgres;

public class PostgresDatabase : IDatabase
{
    private readonly string _connectionString;

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
            var sql = GenerateScriptForDelta(delta);
            await RunScriptAsync(sql, cancellationToken: cancellationToken);
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

        foreach (var index in dependentElements.Where(i => i.Type == PostgresElementTypes.SqlIndex))
        {
            sb.AppendLine();
            sb.Append(GenerateCreateIndexScript(index, tableName));
        }

        return sb.ToString();
    }

    private static string GenerateCreateIndexScript(Element index, string tableName)
    {
        if (index.Name is not string indexName)
        {
            throw new ArgumentException("Indexes must have names");
        }

        var isUnique = index.GetProperty<bool?>(PostgresPropertyNames.IsUnique) == true;
        var indexMethod = index.GetProperty<string>(PostgresPropertyNames.IndexMethod);

        var columnSpecs = index.GetRelationship(PostgresRelationshipNames.ColumnSpecifications);

        if (columnSpecs == null)
        {
            throw new InvalidOperationException($"Index {indexName} has no column specifications");
        }

        var columnText = new List<string>();

        foreach (var columnSpec in columnSpecs.Entries.OfType<Element>()
                     .Where(i => i.Type == PostgresElementTypes.SqlIndexedColumnSpecification))
        {
            var columnReference = columnSpec.GetRelationship(PostgresRelationshipNames.Column)
                ?.Entries.OfType<Reference>().SingleOrDefault();

            if (columnReference == null)
            {
                throw new InvalidOperationException($"Index {indexName} column specification has no column reference");
            }

            // Column references are stored table-qualified (e.g. "film.title"); the
            // CREATE INDEX column list needs the bare column name.
            var columnName = StripTableQualifier(columnReference.Name, tableName);

            var text = columnName;

            if (columnSpec.GetProperty<bool?>(PostgresPropertyNames.IsAscending) == false)
            {
                text += " DESC";
            }

            if (columnSpec.GetProperty<bool?>(PostgresPropertyNames.NullsFirst) is bool nullsFirst)
            {
                text += nullsFirst ? " NULLS FIRST" : " NULLS LAST";
            }

            columnText.Add(text);
        }

        var sb = new StringBuilder();

        sb.Append("CREATE ");

        if (isUnique)
        {
            sb.Append("UNIQUE ");
        }

        sb.Append("INDEX ").Append(indexName).Append(" ON ").Append(tableName);

        if (indexMethod != null)
        {
            sb.Append(" USING ").Append(indexMethod);
        }

        sb.Append(" (").Append(string.Join(", ", columnText)).AppendLine(");");

        return sb.ToString();
    }

    private static string StripTableQualifier(string columnReference, string tableName)
    {
        var prefix = $"{tableName}.";

        return columnReference.StartsWith(prefix, StringComparison.Ordinal)
            ? columnReference[prefix.Length..]
            : columnReference;
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
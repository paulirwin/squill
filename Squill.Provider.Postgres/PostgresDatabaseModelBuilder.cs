using System.Data;
using Squill.Core;

namespace Squill.Provider.Postgres;

public class PostgresDatabaseModelBuilder : IDatabaseModelBuilder
{
    private readonly IDatabase _database;

    public PostgresDatabaseModelBuilder(IDatabase database)
    {
        _database = database;
    }

    public async Task<Model> ExtractModelAsync(CancellationToken cancellationToken = default)
    {
        var model = new Model();

        await _database.ConnectAsync(cancellationToken);

        const string sql = "SELECT * FROM information_schema.tables;";

        var tables = new List<Element>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, cancellationToken: cancellationToken))
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
        var schemaRelationship = table.Relationships.Single(i => i.Name == PostgresRelationshipNames.Schema);
        // HACK.PI: assume built-in public schema for now
        var schema = schemaRelationship.Entries.OfType<Reference>().First().Name;

        const string sql = "SELECT * FROM information_schema.table_constraints " +
            "WHERE table_catalog = @catalog " +
            "AND table_schema = @schema " +
            "AND table_name = @name " +
            "AND constraint_type = 'PRIMARY KEY';";

        var parameters = new[]
        {
            new DatabaseParameter<string>("@catalog", _database.Name),
            new DatabaseParameter<string>("@schema", schema),
            new DatabaseParameter<string>("@name", table.Name!),
        };

        string name, constraintSchema;
        
        await using (var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken))
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
        const string sql = "SELECT * FROM information_schema.constraint_column_usage " +
            "WHERE constraint_schema = @schema " +
            "AND constraint_name = @name;";

        var parameters = new[]
        {
            new DatabaseParameter<string>("@schema", constraintSchema),
            new DatabaseParameter<string>("@name", constraintName),
        };

        await using var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken);

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
        var schemaRelationship = table.Relationships.Single(i => i.Name == PostgresRelationshipNames.Schema);
        // HACK.PI: assume built-in public schema for now
        var schema = schemaRelationship.Entries.OfType<Reference>().First().Name;

        const string sql = "SELECT * FROM information_schema.columns " +
            "WHERE table_catalog = @catalog " +
            "AND table_schema = @schema " +
            "AND table_name = @name;";

        var parameters = new[]
        {
            new DatabaseParameter<string>("@catalog", _database.Name),
            new DatabaseParameter<string>("@schema", schema),
            new DatabaseParameter<string>("@name", table.Name!),
        };

        await using var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken);

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
}
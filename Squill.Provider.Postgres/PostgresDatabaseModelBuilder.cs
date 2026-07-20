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

    // Postgres system catalogs store bare (unquoted) identifiers, so we query with
    // those, but store the canonical quoted SqlName on the model element. This record
    // pairs the two so extraction can do both without re-deriving one from the other.
    private sealed record TableRef(Element Element, string Schema, string BareName);

    public async Task<Model> ExtractModelAsync(CancellationToken cancellationToken = default)
    {
        var model = new Model();

        await _database.ConnectAsync(cancellationToken);

        const string sql = "SELECT * FROM information_schema.tables;";

        var tables = new List<TableRef>();

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
                    Name = SqlName.Object(name),
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
                tables.Add(new TableRef(element, schema, name));
            }
        }

        foreach (var table in tables)
        {
            await ExtractColumnsAsync(table, cancellationToken);
            await ExtractPrimaryKeyAsync(model, table, cancellationToken);
            await ExtractIndexesAsync(model, table, cancellationToken);
        }

        return model;
    }

    private async Task ExtractIndexesAsync(Model model, TableRef table, CancellationToken cancellationToken = default)
    {
        var schema = table.Schema;

        // Table name is stored schema-less (matching the table element's Name) so the
        // IndexedObject reference and column references resolve against it.
        var tableSqlName = SqlName.Object(table.BareName);

        // pg_index.indisprimary / indisunique tell us the index kind; we skip indexes
        // that back a constraint (primary keys, unique constraints) since those are
        // modeled via their constraint, not as standalone SqlIndex elements.
        const string sql = """
            SELECT
                i.relname AS index_name,
                ix.indisunique AS is_unique,
                am.amname AS index_method,
                a.attname AS column_name,
                k.ordinality AS column_ordinal,
                ix.indoption[k.ordinality - 1] AS column_option
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_am am ON am.oid = i.relam
            JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ordinality) ON TRUE
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
            WHERE n.nspname = @schema
              AND t.relname = @name
              AND NOT ix.indisprimary
              AND NOT EXISTS (
                  SELECT 1 FROM pg_constraint c WHERE c.conindid = ix.indexrelid
              )
            ORDER BY i.relname, k.ordinality;
            """;

        var parameters = new[]
        {
            new DatabaseParameter<string>("@schema", schema),
            new DatabaseParameter<string>("@name", table.BareName),
        };

        // Group rows by index so multi-column indexes build a single element.
        var indexes = new Dictionary<string, Element>();

        await using var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var indexName = reader.GetString("index_name");

            if (!indexes.TryGetValue(indexName, out var index))
            {
                var isUnique = reader.GetBoolean("is_unique");
                var indexMethod = reader.GetString("index_method");

                index = new Element(PostgresElementTypes.SqlIndex)
                {
                    Name = SqlName.Object(indexName),
                    Relationships =
                    {
                        new Relationship(PostgresRelationshipNames.ColumnSpecifications),
                        new Relationship(PostgresRelationshipNames.IndexedObject)
                        {
                            new Reference(tableSqlName)
                        }
                    }
                };

                index.Properties.Add(new Property(PostgresPropertyNames.IsUnique, isUnique));
                index.Properties.Add(new Property(PostgresPropertyNames.IndexMethod, indexMethod));

                indexes.Add(indexName, index);
                model.Elements.Add(index);
            }

            var columnName = reader.GetString("column_name");

            var columnSpecification = new Element(PostgresElementTypes.SqlIndexedColumnSpecification)
            {
                Relationships =
                {
                    new Relationship(PostgresRelationshipNames.Column)
                    {
                        new Reference(tableSqlName.Child(columnName))
                    }
                }
            };

            // indoption bit 0x01 = DESC; bit 0x02 = NULLS FIRST (see pg source: indexing.h)
            var columnOption = reader.GetFieldValue<short>("column_option");
            var isDescending = (columnOption & 0x01) != 0;
            var nullsFirst = (columnOption & 0x02) != 0;

            columnSpecification.Properties.Add(new Property(PostgresPropertyNames.IsAscending, !isDescending));
            columnSpecification.Properties.Add(new Property(PostgresPropertyNames.NullsFirst, nullsFirst));

            index.Relationships
                .Single(r => r.Name == PostgresRelationshipNames.ColumnSpecifications)
                .Add(columnSpecification);
        }
    }

    private async Task ExtractPrimaryKeyAsync(Model model, TableRef table, CancellationToken cancellationToken = default)
    {
        var schema = table.Schema;
        var tableSqlName = SqlName.Object(table.BareName);

        const string sql = "SELECT * FROM information_schema.table_constraints " +
            "WHERE table_catalog = @catalog " +
            "AND table_schema = @schema " +
            "AND table_name = @name " +
            "AND constraint_type = 'PRIMARY KEY';";

        var parameters = new[]
        {
            new DatabaseParameter<string>("@catalog", _database.Name),
            new DatabaseParameter<string>("@schema", schema),
            new DatabaseParameter<string>("@name", table.BareName),
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
            Name = SqlName.Object(name),
            Relationships =
            {
                new Relationship(PostgresRelationshipNames.DefiningTable)
                {
                    new Reference(tableSqlName)
                }
            }
        };
        model.Elements.Add(constraint);

        var columnSpec = new Relationship(PostgresRelationshipNames.ColumnSpecifications);
        constraint.Relationships.Add(columnSpec);

        var indexedColumns = new Element(PostgresElementTypes.SqlIndexedColumnSpecification);
        columnSpec.Entries.Add(indexedColumns);

        await ExtractPrimaryKeyColumnsAsync(constraintSchema, name, tableSqlName, indexedColumns, cancellationToken);
    }

    private async Task ExtractPrimaryKeyColumnsAsync(string constraintSchema,
        string constraintName,
        SqlName tableSqlName,
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
                new Reference(tableSqlName.Child(columns[0]))
            });
            return;
        }

        throw new NotImplementedException("Handle zero or multiple PK columns");
    }

    private async Task ExtractColumnsAsync(TableRef table,
        CancellationToken cancellationToken = default)
    {
        var schema = table.Schema;
        var tableSqlName = SqlName.Object(table.BareName);

        const string sql = "SELECT * FROM information_schema.columns " +
            "WHERE table_catalog = @catalog " +
            "AND table_schema = @schema " +
            "AND table_name = @name;";

        var parameters = new[]
        {
            new DatabaseParameter<string>("@catalog", _database.Name),
            new DatabaseParameter<string>("@schema", schema),
            new DatabaseParameter<string>("@name", table.BareName),
        };

        await using var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken);

        var columns = new Relationship(PostgresRelationshipNames.Columns);
        table.Element.Relationships.Add(columns);

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
                Name = tableSqlName.Child(name),
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
using System.Data;
using Squill.Core;
using Squill.PostgresParser.Syntax;

namespace Squill.Provider.Postgres;

public class PostgresDatabaseModelBuilder : IDatabaseModelBuilder
{
    private readonly IDatabase _database;

    public PostgresDatabaseModelBuilder(IDatabase database)
    {
        _database = database;
    }

    // Postgres system catalogs store bare (unquoted) identifiers, so we query with
    // those, but store the canonical SqlName on the model element. This record
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

                var element = PostgresModelFactory.CreateTable(SqlName.Object(name), schema);

                model.Elements.Add(element);
                tables.Add(new TableRef(element, schema, name));
            }
        }

        foreach (var table in tables)
        {
            await ExtractColumnsAsync(table, cancellationToken);
            await ExtractPrimaryKeyAsync(model, table, cancellationToken);
            await ExtractIndexesAsync(model, table, cancellationToken);
            await ExtractForeignKeysAsync(model, table, cancellationToken);
        }

        return model;
    }

    private async Task ExtractForeignKeysAsync(Model model, TableRef table, CancellationToken cancellationToken = default)
    {
        var schema = table.Schema;
        var tableSqlName = SqlName.Object(table.BareName);

        // pg_constraint holds one row per FK. conkey/confkey are parallel arrays of the
        // referencing/referenced column attnums, in key order; unnesting them WITH
        // ORDINALITY and joining back to pg_attribute yields the ordered column pairs.
        // confdeltype/confupdtype are single-char action codes (a/r/c/n/d).
        const string sql = """
            SELECT
                c.conname AS constraint_name,
                rt.relname AS referenced_table,
                c.confdeltype AS delete_action,
                c.confupdtype AS update_action,
                k.ordinality AS key_ordinal,
                la.attname AS column_name,
                fa.attname AS referenced_column
            FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_class rt ON rt.oid = c.confrelid
            JOIN LATERAL unnest(c.conkey, c.confkey) WITH ORDINALITY AS k(attnum, refattnum, ordinality) ON TRUE
            JOIN pg_attribute la ON la.attrelid = c.conrelid AND la.attnum = k.attnum
            JOIN pg_attribute fa ON fa.attrelid = c.confrelid AND fa.attnum = k.refattnum
            WHERE c.contype = 'f'
              AND n.nspname = @schema
              AND t.relname = @name
            ORDER BY c.conname, k.ordinality;
            """;

        var parameters = new[]
        {
            new DatabaseParameter<string>("@schema", schema),
            new DatabaseParameter<string>("@name", table.BareName),
        };

        var foreignKeys = new Dictionary<string, ForeignKeyAccumulator>();
        var order = new List<string>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var constraintName = reader.GetString("constraint_name");

                if (!foreignKeys.TryGetValue(constraintName, out var accumulator))
                {
                    var referencedTable = SqlName.Object(reader.GetString("referenced_table"));

                    accumulator = new ForeignKeyAccumulator(
                        referencedTable,
                        MapReferentialAction(reader.GetFieldValue<char>("delete_action")),
                        MapReferentialAction(reader.GetFieldValue<char>("update_action")));

                    foreignKeys.Add(constraintName, accumulator);
                    order.Add(constraintName);
                }

                accumulator.Columns.Add(tableSqlName.Child(reader.GetString("column_name")));
                accumulator.ReferencedColumns.Add(
                    accumulator.ReferencedTable.Child(reader.GetString("referenced_column")));
            }
        }

        foreach (var constraintName in order)
        {
            var accumulator = foreignKeys[constraintName];

            model.Elements.Add(PostgresModelFactory.CreateForeignKey(
                SqlName.Object(constraintName),
                tableSqlName,
                accumulator.Columns,
                accumulator.ReferencedTable,
                accumulator.ReferencedColumns,
                accumulator.OnDelete,
                accumulator.OnUpdate));
        }
    }

    // Accumulates the ordered column pairs of one foreign key across result rows.
    private sealed class ForeignKeyAccumulator
    {
        public ForeignKeyAccumulator(SqlName referencedTable,
            ReferentialAction onDelete,
            ReferentialAction onUpdate)
        {
            ReferencedTable = referencedTable;
            OnDelete = onDelete;
            OnUpdate = onUpdate;
        }

        public SqlName ReferencedTable { get; }
        public ReferentialAction OnDelete { get; }
        public ReferentialAction OnUpdate { get; }
        public List<SqlName> Columns { get; } = new();
        public List<SqlName> ReferencedColumns { get; } = new();
    }

    // pg_constraint stores the ON DELETE/UPDATE action as a single char.
    private static ReferentialAction MapReferentialAction(char code)
        => code switch
        {
            'a' => ReferentialAction.NoAction,
            'r' => ReferentialAction.Restrict,
            'c' => ReferentialAction.Cascade,
            'n' => ReferentialAction.SetNull,
            'd' => ReferentialAction.SetDefault,
            _ => throw new InvalidOperationException($"Unknown pg_constraint action code: {code}"),
        };

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

        // Accumulate rows per index (ordered by column ordinal in the query) so a
        // multi-column index is built as a single element via the factory.
        var indexRows = new Dictionary<string, (bool IsUnique, string Method, List<PostgresModelFactory.IndexedColumn> Columns)>();
        var order = new List<string>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var indexName = reader.GetString("index_name");

                if (!indexRows.TryGetValue(indexName, out var entry))
                {
                    entry = (reader.GetBoolean("is_unique"), reader.GetString("index_method"), new());
                    indexRows.Add(indexName, entry);
                    order.Add(indexName);
                }

                var columnName = reader.GetString("column_name");

                // indoption bit 0x01 = DESC; bit 0x02 = NULLS FIRST (see pg source: indexing.h)
                var columnOption = reader.GetFieldValue<short>("column_option");
                var isDescending = (columnOption & 0x01) != 0;
                var nullsFirst = (columnOption & 0x02) != 0;

                entry.Columns.Add(new PostgresModelFactory.IndexedColumn(
                    tableSqlName.Child(columnName), IsAscending: !isDescending, NullsFirst: nullsFirst));
            }
        }

        foreach (var indexName in order)
        {
            var (isUnique, method, columns) = indexRows[indexName];

            model.Elements.Add(PostgresModelFactory.CreateIndex(
                SqlName.Object(indexName), tableSqlName, isUnique, method, columns));
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

        var columns = await ExtractPrimaryKeyColumnsAsync(constraintSchema, name, tableSqlName, cancellationToken);

        model.Elements.Add(PostgresModelFactory.CreatePrimaryKey(
            SqlName.Object(name), tableSqlName, columns));
    }

    private async Task<IReadOnlyList<PostgresModelFactory.IndexedColumn>> ExtractPrimaryKeyColumnsAsync(
        string constraintSchema,
        string constraintName,
        SqlName tableSqlName,
        CancellationToken cancellationToken = default)
    {
        // ordinal_position orders the columns of a composite primary key.
        const string sql = "SELECT ccu.column_name " +
            "FROM information_schema.constraint_column_usage ccu " +
            "JOIN information_schema.key_column_usage kcu " +
            "  ON kcu.constraint_schema = ccu.constraint_schema " +
            "  AND kcu.constraint_name = ccu.constraint_name " +
            "  AND kcu.column_name = ccu.column_name " +
            "WHERE ccu.constraint_schema = @schema " +
            "AND ccu.constraint_name = @name " +
            "ORDER BY kcu.ordinal_position;";

        var parameters = new[]
        {
            new DatabaseParameter<string>("@schema", constraintSchema),
            new DatabaseParameter<string>("@name", constraintName),
        };

        await using var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken);

        var columns = new List<PostgresModelFactory.IndexedColumn>();

        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new PostgresModelFactory.IndexedColumn(tableSqlName.Child(reader.GetString("column_name"))));
        }

        return columns;
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
            var isIdentity = reader.GetString("is_identity") == "YES";

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

            if (isIdentity)
            {
                // identity_generation is ALWAYS or BY DEFAULT for identity columns
                // (non-null whenever is_identity is YES).
                var identityGeneration = reader.GetString("identity_generation");

                column.Properties.Add(new Property(PostgresPropertyNames.IsIdentity, true));
                column.Properties.Add(new Property(PostgresPropertyNames.IdentityGeneration,
                    identityGeneration == "ALWAYS" ? "Always" : "ByDefault"));
            }

            columns.Entries.Add(column);
        }
    }
}
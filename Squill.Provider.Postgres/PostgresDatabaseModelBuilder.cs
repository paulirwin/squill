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

        // Schemas and extensions are extracted first so they lead the model's element
        // order. A table lives in a schema and may use a type provided by an extension
        // (e.g. pgvector's vector), so on publish the CREATE SCHEMA / CREATE EXTENSION must
        // run before the CREATE TABLE that depends on them.
        await ExtractSchemasAsync(model, cancellationToken);
        await ExtractExtensionsAsync(model, cancellationToken);

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

    private async Task ExtractSchemasAsync(Model model, CancellationToken cancellationToken = default)
    {
        // Emit a SqlSchema element for each user-declared schema. 'public' exists in every
        // database by default and is not a declared object (users don't write CREATE
        // SCHEMA public), so it is skipped — matching the parser builder, which only emits
        // SqlSchema for an explicit CREATE SCHEMA. System schemas (pg_*, information_schema)
        // are likewise excluded. This keeps a parsed model hash-matching an extracted one.
        const string sql =
            "SELECT schema_name FROM information_schema.schemata "
            + "WHERE schema_name NOT IN ('public', 'information_schema') "
            + "AND schema_name NOT LIKE 'pg_%' ORDER BY schema_name;";

        var schemas = new List<string>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, cancellationToken: cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                schemas.Add(reader.GetString("schema_name"));
            }
        }

        foreach (var schemaName in schemas)
        {
            model.Elements.Add(PostgresModelFactory.CreateSchema(SqlName.Object(schemaName)));
        }
    }

    private async Task ExtractExtensionsAsync(Model model, CancellationToken cancellationToken = default)
    {
        // pg_extension lists every installed extension. plpgsql is created in every
        // database by default and is not part of the declared schema, so it is skipped
        // so a parsed model (which won't declare it) hash-matches the extracted one.
        // Version is intentionally not stored: the installed version is not part of the
        // desired-state identity (see PostgresModelFactory.CreateExtension).
        const string sql = "SELECT extname FROM pg_extension WHERE extname <> 'plpgsql' ORDER BY extname;";

        var extensions = new List<string>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, cancellationToken: cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                extensions.Add(reader.GetString("extname"));
            }
        }

        foreach (var extensionName in extensions)
        {
            model.Elements.Add(PostgresModelFactory.CreateExtension(SqlName.Object(extensionName)));
        }
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

    // Extracts a single integer type modifier from format_type() output, e.g. the 3 in
    // "vector(3)". Returns false when the type carries no modifier (e.g. a bare "vector")
    // or a non-integer/multi-part modifier we don't model here.
    private static bool TryParseTypeModifier(string formattedType, out int modifier)
    {
        modifier = 0;

        var open = formattedType.IndexOf('(');
        var close = formattedType.IndexOf(')');

        if (open < 0 || close < open)
        {
            return false;
        }

        var inner = formattedType[(open + 1)..close];

        return int.TryParse(inner, out modifier);
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
        // Per-column operator class: indclass holds the opclass OID for each key column.
        // We only surface a non-default opclass (opcdefault = false), matching the parser
        // builder, which stores an opclass only when one is written explicitly.
        // Storage parameters (the WITH clause) come from the index relation's reloptions,
        // a text[] of "name=value" entries rendered to a canonical comma-separated string.
        const string sql = """
            SELECT
                i.relname AS index_name,
                ix.indisunique AS is_unique,
                am.amname AS index_method,
                pg_get_expr(ix.indpred, ix.indrelid) AS filter_predicate,
                a.attname AS column_name,
                k.ordinality AS column_ordinal,
                ix.indoption[k.ordinality - 1] AS column_option,
                CASE WHEN oc.opcdefault THEN NULL ELSE oc.opcname END AS operator_class,
                array_to_string(i.reloptions, ', ') AS storage_parameters
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_am am ON am.oid = i.relam
            JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ordinality) ON TRUE
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
            JOIN pg_opclass oc ON oc.oid = ix.indclass[k.ordinality - 1]
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
        var indexRows = new Dictionary<string, (bool IsUnique, string Method, string? FilterPredicate, string? StorageParameters, List<PostgresModelFactory.IndexedColumn> Columns)>();
        var order = new List<string>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var indexName = reader.GetString("index_name");

                if (!indexRows.TryGetValue(indexName, out var entry))
                {
                    // pg_get_expr returns NULL for a non-partial index; a partial index
                    // yields its canonical WHERE predicate text.
                    var filterPredicate = reader.IsDBNull("filter_predicate")
                        ? null
                        : reader.GetString("filter_predicate");

                    // reloptions is NULL for an index with no WITH clause; array_to_string
                    // of an empty array is an empty string, so treat both as "none".
                    var storageParameters = reader.IsDBNull("storage_parameters")
                        ? null
                        : reader.GetString("storage_parameters") is { Length: > 0 } s ? s : null;

                    entry = (reader.GetBoolean("is_unique"), reader.GetString("index_method"), filterPredicate, storageParameters, new());
                    indexRows.Add(indexName, entry);
                    order.Add(indexName);
                }

                var columnName = reader.GetString("column_name");

                // Only btree supports per-column ASC/DESC and NULLS ordering; other access
                // methods (e.g. hnsw) reject those options, and their indoption bits are
                // always 0. Surfacing direction/null-order only for btree keeps the model
                // free of ordering the emitted DDL can't legally carry.
                bool? isAscending = null;
                bool? nullsFirst = null;

                if (entry.Method == "btree")
                {
                    // indoption bit 0x01 = DESC; bit 0x02 = NULLS FIRST (see pg source: indexing.h)
                    var columnOption = reader.GetFieldValue<short>("column_option");
                    isAscending = (columnOption & 0x01) == 0;
                    nullsFirst = (columnOption & 0x02) != 0;
                }

                var operatorClass = reader.IsDBNull("operator_class")
                    ? null
                    : reader.GetString("operator_class");

                entry.Columns.Add(new PostgresModelFactory.IndexedColumn(
                    tableSqlName.Child(columnName),
                    IsAscending: isAscending,
                    NullsFirst: nullsFirst,
                    OperatorClass: operatorClass));
            }
        }

        foreach (var indexName in order)
        {
            var (isUnique, method, filterPredicate, storageParameters, columns) = indexRows[indexName];

            model.Elements.Add(PostgresModelFactory.CreateIndex(
                SqlName.Object(indexName), tableSqlName, isUnique, method, columns,
                filterPredicate, storageParameters, schema));
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

        // information_schema.columns reports data_type = 'USER-DEFINED' for extension
        // types like pgvector's vector, with the real type name in udt_name. The
        // dimension of a vector(n) lives in pg_attribute.atttypmod, which
        // information_schema does not expose, so we join pg_catalog and use
        // format_type() — PostgreSQL's canonical type renderer — to recover it as text
        // (e.g. "vector(3)"), then parse out the modifier.
        const string sql = """
            SELECT
                c.column_name,
                c.is_nullable,
                c.data_type,
                c.character_maximum_length,
                c.is_identity,
                c.identity_generation,
                c.udt_name,
                format_type(a.atttypid, a.atttypmod) AS formatted_type
            FROM information_schema.columns c
            JOIN pg_namespace n ON n.nspname = c.table_schema
            JOIN pg_class t ON t.relname = c.table_name AND t.relnamespace = n.oid
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attname = c.column_name
            WHERE c.table_catalog = @catalog
              AND c.table_schema = @schema
              AND c.table_name = @name
            ORDER BY c.ordinal_position;
            """;

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

            // For a user-defined type the canonical name is udt_name (e.g. "vector"), not
            // the generic "USER-DEFINED" that data_type reports. The type modifier (a
            // vector's dimension) is recovered from the format_type() text and mapped to
            // the same Length property the parser builder uses, so both sides hash-match.
            if (dataType == "USER-DEFINED")
            {
                dataType = reader.GetString("udt_name");

                var formattedType = reader.GetString("formatted_type");

                if (TryParseTypeModifier(formattedType, out var modifier))
                {
                    maxLength = modifier;
                }
            }

            var typeElement = new Element(PostgresElementTypes.SqlTypeSpecifier)
            {
                Relationships =
                {
                    new Relationship(PostgresRelationshipNames.Type)
                    {
                        Entries =
                        {
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
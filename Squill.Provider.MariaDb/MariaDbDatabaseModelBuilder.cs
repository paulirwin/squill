using Squill.Core;
using Squill.MariaDbParser.Syntax;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Extracts a <see cref="Model"/> from a live MariaDB (or MySQL) database by querying
/// <c>information_schema</c>. Every element is built through <see cref="MariaDbModelFactory"/>
/// so a database-extracted model hash-matches one parsed from declarative SQL. Scopes all
/// queries to the connected database (a MariaDB database is the schema namespace).
/// </summary>
public class MariaDbDatabaseModelBuilder : IDatabaseModelBuilder
{
    private readonly IDatabase _database;

    public MariaDbDatabaseModelBuilder(IDatabase database)
    {
        _database = database;
    }

    // MariaDB information_schema stores bare identifiers; we store the canonical SqlName on
    // the element. This pairs the two so extraction can do both.
    private sealed record TableRef(Element Element, string BareName);

    public async Task<Model> ExtractModelAsync(CancellationToken cancellationToken = default)
    {
        var model = new Model();

        await _database.ConnectAsync(cancellationToken);

        const string sql =
            "SELECT TABLE_NAME FROM information_schema.TABLES "
            + "WHERE TABLE_SCHEMA = @db AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME;";

        var dbParam = new[] { new DatabaseParameter<string>("@db", _database.Name) };

        var tables = new List<TableRef>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, dbParam, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString("TABLE_NAME");

                var element = MariaDbModelFactory.CreateTable(SqlName.Object(name));

                tables.Add(new TableRef(element, name));
            }
        }

        // Emit each table immediately followed by its dependents (primary key, indexes,
        // foreign keys), so the element order matches the parser-based builder, which yields
        // a table and its dependents together. The Merkle hash is order-sensitive, so the
        // two builders must agree on ordering for a parsed model to hash-match an extracted
        // one.
        foreach (var table in tables)
        {
            model.Elements.Add(table.Element);

            await ExtractColumnsAsync(table, cancellationToken);
            await ExtractPrimaryKeyAsync(model, table, cancellationToken);
            await ExtractIndexesAsync(model, table, cancellationToken);
            await ExtractForeignKeysAsync(model, table, cancellationToken);
        }

        return model;
    }

    private async Task ExtractColumnsAsync(TableRef table, CancellationToken cancellationToken = default)
    {
        var tableSqlName = SqlName.Object(table.BareName);

        const string sql = """
            SELECT
                COLUMN_NAME,
                IS_NULLABLE,
                DATA_TYPE,
                COLUMN_TYPE,
                CHARACTER_MAXIMUM_LENGTH,
                NUMERIC_PRECISION,
                NUMERIC_SCALE,
                EXTRA,
                COLUMN_DEFAULT
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @name
            ORDER BY ORDINAL_POSITION;
            """;

        var parameters = new[]
        {
            new DatabaseParameter<string>("@db", _database.Name),
            new DatabaseParameter<string>("@name", table.BareName),
        };

        await using var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken);

        var columns = new Relationship(MariaDbRelationshipNames.Columns);
        table.Element.Relationships.Add(columns);

        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString("COLUMN_NAME");
            var nullable = reader.GetString("IS_NULLABLE") == "YES";
            var dataType = reader.GetString("DATA_TYPE").ToLowerInvariant();
            var columnType = reader.GetString("COLUMN_TYPE").ToLowerInvariant();
            // MariaDB and MySQL disagree on the CLR type of these information_schema numeric
            // columns (MariaDB returns ulong, MySQL long), so read them engine-agnostically.
            var maxLength = reader.GetNullableInt64("CHARACTER_MAXIMUM_LENGTH");
            var numericPrecision = reader.GetNullableInt64("NUMERIC_PRECISION");
            var numericScale = reader.GetNullableInt64("NUMERIC_SCALE");
            var extra = reader.GetString("EXTRA");
            var isAutoIncrement = extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase);
            var isUnsigned = columnType.Contains("unsigned", StringComparison.Ordinal);

            var typeElement = new Element(MariaDbElementTypes.SqlTypeSpecifier)
            {
                Relationships =
                {
                    new Relationship(MariaDbRelationshipNames.Type)
                    {
                        Entries =
                        {
                            new Reference(dataType) { ExternalSource = "BuiltIns" }
                        }
                    }
                }
            };

            // A character type carries its length; a numeric(p,s) type carries precision and
            // scale. These mirror what the parser builder records so both sides hash-match.
            if (IsCharacterType(dataType) && maxLength.HasValue)
            {
                typeElement.Properties.Add(new Property(MariaDbPropertyNames.Length, (int)maxLength.Value));
            }
            else if (IsDecimalType(dataType) && numericPrecision.HasValue)
            {
                typeElement.Properties.Add(
                    new Property(MariaDbPropertyNames.Precision, (long)numericPrecision.Value));
                typeElement.Properties.Add(
                    new Property(MariaDbPropertyNames.Scale, (long)(numericScale ?? 0)));
            }

            if (isUnsigned)
            {
                typeElement.Properties.Add(new Property(MariaDbPropertyNames.IsUnsigned, true));
            }

            var column = new Element(MariaDbElementTypes.SqlSimpleColumn)
            {
                Name = tableSqlName.Child(name),
                Relationships =
                {
                    new Relationship(MariaDbRelationshipNames.TypeSpecifier)
                    {
                        Entries = { typeElement }
                    }
                }
            };

            if (!nullable)
            {
                column.Properties.Add(new Property(MariaDbPropertyNames.IsNullable, false));
            }

            if (isAutoIncrement)
            {
                column.Properties.Add(new Property(MariaDbPropertyNames.IsAutoIncrement, true));
            }

            var columnDefault = reader.IsDBNull(reader.GetOrdinal("COLUMN_DEFAULT"))
                ? null
                : reader.GetString("COLUMN_DEFAULT");

            if (MariaDbDefaultValue.FromDatabaseText(columnDefault, IsCharacterType(dataType)) is { } defaultValue)
            {
                column.Properties.Add(new Property(MariaDbPropertyNames.DefaultValue, defaultValue));
            }

            columns.Entries.Add(column);
        }
    }

    private async Task ExtractPrimaryKeyAsync(Model model, TableRef table, CancellationToken cancellationToken = default)
    {
        var tableSqlName = SqlName.Object(table.BareName);

        // MariaDB always names the primary key constraint 'PRIMARY'. Its columns come from
        // STATISTICS (INDEX_NAME = 'PRIMARY'), ordered by SEQ_IN_INDEX.
        const string sql = """
            SELECT COLUMN_NAME
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @name AND INDEX_NAME = 'PRIMARY'
            ORDER BY SEQ_IN_INDEX;
            """;

        var parameters = new[]
        {
            new DatabaseParameter<string>("@db", _database.Name),
            new DatabaseParameter<string>("@name", table.BareName),
        };

        var columns = new List<MariaDbModelFactory.IndexedColumn>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(new MariaDbModelFactory.IndexedColumn(
                    tableSqlName.Child(reader.GetString("COLUMN_NAME"))));
            }
        }

        if (columns.Count == 0)
        {
            return; // no PK
        }

        model.Elements.Add(MariaDbModelFactory.CreatePrimaryKey(
            tableSqlName.Sibling("PRIMARY"), tableSqlName, columns));
    }

    private async Task ExtractIndexesAsync(Model model, TableRef table, CancellationToken cancellationToken = default)
    {
        var tableSqlName = SqlName.Object(table.BareName);

        // Standalone indexes: everything in STATISTICS except the PRIMARY key and any index
        // that backs a unique/foreign-key constraint we model separately. We skip PRIMARY
        // (modeled as the PK) but DO surface UNIQUE indexes as SqlIndex with IsUnique.
        // NON_UNIQUE = 0 means unique. INDEX_TYPE is BTREE / HASH / FULLTEXT / SPATIAL.
        // COLLATION is 'A' (ascending), 'D' (descending), or NULL (unordered).
        const string sql = """
            SELECT
                INDEX_NAME,
                NON_UNIQUE,
                INDEX_TYPE,
                SEQ_IN_INDEX,
                COLUMN_NAME,
                COLLATION
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @name AND INDEX_NAME <> 'PRIMARY'
            ORDER BY INDEX_NAME, SEQ_IN_INDEX;
            """;

        var parameters = new[]
        {
            new DatabaseParameter<string>("@db", _database.Name),
            new DatabaseParameter<string>("@name", table.BareName),
        };

        var indexRows = new Dictionary<string, (bool IsUnique, string Method, List<MariaDbModelFactory.IndexedColumn> Columns)>();
        var order = new List<string>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var indexName = reader.GetString("INDEX_NAME");

                if (!indexRows.TryGetValue(indexName, out var entry))
                {
                    var nonUnique = reader.GetNullableInt64("NON_UNIQUE") ?? 1;
                    var indexType = reader.GetString("INDEX_TYPE").ToUpperInvariant();

                    entry = (nonUnique == 0, indexType, new());
                    indexRows.Add(indexName, entry);
                    order.Add(indexName);
                }

                var columnName = reader.GetString("COLUMN_NAME");

                // COLLATION 'D' marks a descending column; 'A' ascending; NULL unordered.
                bool? isAscending = reader.IsDBNull(reader.GetOrdinal("COLLATION"))
                    ? null
                    : reader.GetString("COLLATION") == "A";

                entry.Columns.Add(new MariaDbModelFactory.IndexedColumn(
                    tableSqlName.Child(columnName), isAscending));
            }
        }

        // Index names that back a foreign key: MariaDB auto-creates an index for each FK,
        // which we do not surface as a standalone SqlIndex (it is implied by the FK).
        var foreignKeyIndexNames = await GetForeignKeyIndexNamesAsync(table.BareName, cancellationToken);

        foreach (var indexName in order)
        {
            if (foreignKeyIndexNames.Contains(indexName))
            {
                continue;
            }

            var (isUnique, method, columns) = indexRows[indexName];

            model.Elements.Add(MariaDbModelFactory.CreateIndex(
                SqlName.Object(indexName), tableSqlName, isUnique, method, columns));
        }
    }

    private async Task<HashSet<string>> GetForeignKeyIndexNamesAsync(string tableName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CONSTRAINT_NAME
            FROM information_schema.TABLE_CONSTRAINTS
            WHERE CONSTRAINT_SCHEMA = @db AND TABLE_NAME = @name AND CONSTRAINT_TYPE = 'FOREIGN KEY';
            """;

        var parameters = new[]
        {
            new DatabaseParameter<string>("@db", _database.Name),
            new DatabaseParameter<string>("@name", tableName),
        };

        var names = new HashSet<string>(StringComparer.Ordinal);

        await using var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            names.Add(reader.GetString("CONSTRAINT_NAME"));
        }

        return names;
    }

    private async Task ExtractForeignKeysAsync(Model model, TableRef table, CancellationToken cancellationToken = default)
    {
        var tableSqlName = SqlName.Object(table.BareName);

        // KEY_COLUMN_USAGE gives the referencing/referenced column pairs (ordered by
        // ORDINAL_POSITION); REFERENTIAL_CONSTRAINTS gives the ON DELETE / ON UPDATE rules.
        const string sql = """
            SELECT
                kcu.CONSTRAINT_NAME,
                kcu.COLUMN_NAME,
                kcu.REFERENCED_TABLE_NAME,
                kcu.REFERENCED_COLUMN_NAME,
                kcu.ORDINAL_POSITION,
                rc.DELETE_RULE,
                rc.UPDATE_RULE
            FROM information_schema.KEY_COLUMN_USAGE kcu
            JOIN information_schema.REFERENTIAL_CONSTRAINTS rc
              ON rc.CONSTRAINT_SCHEMA = kcu.CONSTRAINT_SCHEMA
             AND rc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
            WHERE kcu.CONSTRAINT_SCHEMA = @db
              AND kcu.TABLE_NAME = @name
              AND kcu.REFERENCED_TABLE_NAME IS NOT NULL
            ORDER BY kcu.CONSTRAINT_NAME, kcu.ORDINAL_POSITION;
            """;

        var parameters = new[]
        {
            new DatabaseParameter<string>("@db", _database.Name),
            new DatabaseParameter<string>("@name", table.BareName),
        };

        var foreignKeys = new Dictionary<string, ForeignKeyAccumulator>();
        var order = new List<string>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var constraintName = reader.GetString("CONSTRAINT_NAME");

                if (!foreignKeys.TryGetValue(constraintName, out var accumulator))
                {
                    var referencedTable = SqlName.Object(reader.GetString("REFERENCED_TABLE_NAME"));

                    accumulator = new ForeignKeyAccumulator(
                        referencedTable,
                        MapReferentialAction(reader.GetString("DELETE_RULE")),
                        MapReferentialAction(reader.GetString("UPDATE_RULE")));

                    foreignKeys.Add(constraintName, accumulator);
                    order.Add(constraintName);
                }

                accumulator.Columns.Add(tableSqlName.Child(reader.GetString("COLUMN_NAME")));
                accumulator.ReferencedColumns.Add(
                    accumulator.ReferencedTable.Child(reader.GetString("REFERENCED_COLUMN_NAME")));
            }
        }

        foreach (var constraintName in order)
        {
            var accumulator = foreignKeys[constraintName];

            model.Elements.Add(MariaDbModelFactory.CreateForeignKey(
                SqlName.Object(constraintName),
                tableSqlName,
                accumulator.Columns,
                accumulator.ReferencedTable,
                accumulator.ReferencedColumns,
                accumulator.OnDelete,
                accumulator.OnUpdate));
        }
    }

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

    // REFERENTIAL_CONSTRAINTS reports the rule as text (e.g. "CASCADE", "SET NULL",
    // "RESTRICT", "NO ACTION"). MariaDB treats NO ACTION as RESTRICT and reports RESTRICT.
    private static ReferentialAction MapReferentialAction(string rule)
        => rule.ToUpperInvariant() switch
        {
            "RESTRICT" => ReferentialAction.Restrict,
            "NO ACTION" => ReferentialAction.Restrict,
            "CASCADE" => ReferentialAction.Cascade,
            "SET NULL" => ReferentialAction.SetNull,
            "SET DEFAULT" => ReferentialAction.SetDefault,
            _ => throw new InvalidOperationException($"Unknown referential rule: {rule}"),
        };

    private static bool IsCharacterType(string dataType)
        => dataType is "char" or "varchar";

    private static bool IsDecimalType(string dataType)
        => dataType is "decimal" or "numeric";
}

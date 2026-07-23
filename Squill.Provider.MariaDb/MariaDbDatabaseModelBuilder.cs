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

            // Foreign keys precede indexes, matching the parser: a table's constraints are
            // written in its CREATE TABLE, while a standalone index comes from a separate
            // CREATE INDEX statement that follows it.
            await ExtractForeignKeysAsync(model, table, cancellationToken);
            await ExtractIndexesAsync(model, table, cancellationToken);
        }

        // Views come after tables (a view selects from them) and before procedures, whose
        // bodies may in turn query a view. The Merkle hash is order-sensitive, so this
        // matches the order the parser-based builder produces.
        await ExtractViewsAsync(model, cancellationToken);

        // Routines (procedures and functions) come last, matching the parser-based builder:
        // a routine body may reference any table, so on publish its CREATE must run after the
        // tables it uses. Procedures and functions are ordered together by name.
        await ExtractRoutinesAsync(model, cancellationToken);

        return model;
    }

    private async Task ExtractViewsAsync(Model model, CancellationToken cancellationToken = default)
    {
        // A view's columns are read from information_schema.COLUMNS in ordinal order, which
        // is the shape both engines report for the deployed view.
        //
        // The view's own query (VIEW_DEFINITION) is deliberately NOT read. MariaDB and
        // MySQL each rewrite the query when they store it — and differently from each other
        // — so it could never match the declared source. A view's modeled identity is its
        // name and column list instead; see MariaDbModelFactory.CreateView.
        const string sql =
            """
            SELECT v.TABLE_NAME,
                   (SELECT GROUP_CONCAT(c.COLUMN_NAME ORDER BY c.ORDINAL_POSITION SEPARATOR 0x1e)
                    FROM information_schema.COLUMNS c
                    WHERE c.TABLE_SCHEMA = v.TABLE_SCHEMA
                      AND c.TABLE_NAME = v.TABLE_NAME) AS COLUMN_NAMES
            FROM information_schema.VIEWS v
            WHERE v.TABLE_SCHEMA = @db
            ORDER BY v.TABLE_NAME;
            """;

        var dbParam = new[] { new DatabaseParameter<string>("@db", _database.Name) };

        var views = new List<Element>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, dbParam, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString("TABLE_NAME");
                var columnNames = reader.IsDBNull(reader.GetOrdinal("COLUMN_NAMES"))
                    ? string.Empty
                    : reader.GetString("COLUMN_NAMES");

                views.Add(MariaDbModelFactory.CreateView(
                    SqlName.Object(name),
                    columnNames.Length == 0 ? [] : columnNames.Split(ViewColumnSeparator),
                    // The database's own query text is never modeled — see above.
                    definition: null));
            }
        }

        foreach (var view in views)
        {
            model.Elements.Add(view);
        }
    }

    // Column names are joined with a record separator, which cannot occur in an identifier.
    private const char ViewColumnSeparator = '\u001e';

    private async Task ExtractRoutinesAsync(Model model, CancellationToken cancellationToken = default)
    {
        // Procedures and functions are read together, ordered by name, so a single ordering
        // covers both. Both engines return ROUTINE_DEFINITION verbatim, so the body needs no
        // canonicalization on either side. The catalog has no notion of declaration order, so
        // the parser-based builder must adopt this same name order — see MoveRoutinesToEnd.
        //
        // DATA_TYPE / DTD_IDENTIFIER are the function's return type (empty for a procedure).
        // The type is rebuilt from DATA_TYPE plus the numeric columns (as for parameters),
        // because the two engines spell DTD_IDENTIFIER differently for integers.
        const string routineSql =
            """
            SELECT ROUTINE_NAME, ROUTINE_TYPE, ROUTINE_DEFINITION, IS_DETERMINISTIC,
                   SQL_DATA_ACCESS, SECURITY_TYPE,
                   DATA_TYPE, DTD_IDENTIFIER,
                   CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE
            FROM information_schema.ROUTINES
            WHERE ROUTINE_SCHEMA = @db AND ROUTINE_TYPE IN ('PROCEDURE', 'FUNCTION')
            ORDER BY ROUTINE_NAME;
            """;

        var dbParam = new[] { new DatabaseParameter<string>("@db", _database.Name) };

        var routines = new List<(string Name, bool IsFunction, string Body, string? ReturnType,
            bool IsDeterministic, string SqlDataAccess, bool IsSecurityInvoker)>();

        await using (var reader = await _database.RunScriptReaderAsync(
            routineSql, dbParam, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString("ROUTINE_NAME");
                var isFunction = reader.GetString("ROUTINE_TYPE") == "FUNCTION";

                // ROUTINE_DEFINITION is NULL when the connected user lacks the privileges to
                // read a routine's body. Deploying the resulting model would silently replace
                // the routine with an empty one, so fail loudly instead.
                if (reader.IsDBNull(reader.GetOrdinal("ROUTINE_DEFINITION")))
                {
                    throw new InvalidOperationException(
                        $"The body of routine '{name}' could not be read. The connected user "
                        + "needs privileges on the routine to extract its definition.");
                }

                string? returnType = null;
                if (isFunction)
                {
                    var dataType = reader.GetString("DATA_TYPE").ToLowerInvariant();
                    var dtd = reader.GetString("DTD_IDENTIFIER").ToLowerInvariant();
                    var maxLength = reader.GetNullableInt64("CHARACTER_MAXIMUM_LENGTH");
                    var precision = reader.GetNullableInt64("NUMERIC_PRECISION");
                    var scale = reader.GetNullableInt64("NUMERIC_SCALE");

                    returnType = NormalizeParameterType(dataType, dtd, maxLength, precision, scale);
                }

                routines.Add((
                    name,
                    isFunction,
                    reader.GetString("ROUTINE_DEFINITION"),
                    returnType,
                    reader.GetString("IS_DETERMINISTIC") == "YES",
                    reader.GetString("SQL_DATA_ACCESS"),
                    reader.GetString("SECURITY_TYPE") == "INVOKER"));
            }
        }

        foreach (var routine in routines)
        {
            var parameters = await ExtractProcedureParametersAsync(routine.Name, cancellationToken);

            model.Elements.Add(routine.IsFunction
                ? MariaDbModelFactory.CreateFunction(
                    SqlName.Object(routine.Name),
                    routine.ReturnType!,
                    routine.Body,
                    parameters,
                    routine.IsDeterministic,
                    routine.SqlDataAccess,
                    routine.IsSecurityInvoker)
                : MariaDbModelFactory.CreateProcedure(
                    SqlName.Object(routine.Name),
                    routine.Body,
                    parameters,
                    routine.IsDeterministic,
                    routine.SqlDataAccess,
                    routine.IsSecurityInvoker));
        }
    }

    private async Task<IReadOnlyList<MariaDbModelFactory.ProcedureParameter>>
        ExtractProcedureParametersAsync(string routineName, CancellationToken cancellationToken = default)
    {
        // The type is rebuilt from DATA_TYPE plus length/precision rather than read from
        // DTD_IDENTIFIER, because the two engines spell that column differently: MariaDB
        // reports an integer's display width (int(11)) and MySQL does not (int). DATA_TYPE
        // and the numeric columns agree on both, so building from them keeps one model
        // shape across engines. See MariaDbTypeNormalizer.
        //
        // A procedure's own row has ORDINAL_POSITION 0 with a NULL name (it is the return
        // value slot, used by functions), so parameters start at 1.
        const string sql =
            """
            SELECT PARAMETER_MODE, PARAMETER_NAME, DATA_TYPE,
                   CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, DTD_IDENTIFIER
            FROM information_schema.PARAMETERS
            WHERE SPECIFIC_SCHEMA = @db AND SPECIFIC_NAME = @routine AND ORDINAL_POSITION > 0
            ORDER BY ORDINAL_POSITION;
            """;

        var parameters = new IDatabaseParameter[]
        {
            new DatabaseParameter<string>("@db", _database.Name),
            new DatabaseParameter<string>("@routine", routineName),
        };

        var result = new List<MariaDbModelFactory.ProcedureParameter>();

        await using var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var dataType = reader.GetString("DATA_TYPE").ToLowerInvariant();
            var dtd = reader.GetString("DTD_IDENTIFIER").ToLowerInvariant();

            // MariaDB and MySQL disagree on the CLR type of these information_schema numeric
            // columns (MariaDB returns ulong, MySQL long), so read them engine-agnostically.
            var maxLength = reader.GetNullableInt64("CHARACTER_MAXIMUM_LENGTH");
            var precision = reader.GetNullableInt64("NUMERIC_PRECISION");
            var scale = reader.GetNullableInt64("NUMERIC_SCALE");

            result.Add(new MariaDbModelFactory.ProcedureParameter(
                reader.GetString("PARAMETER_MODE"),
                reader.GetString("PARAMETER_NAME"),
                NormalizeParameterType(dataType, dtd, maxLength, precision, scale)));
        }

        return result;
    }

    // Rebuilds a parameter's canonical type text from the catalog's engine-agnostic columns.
    private static string NormalizeParameterType(
        string dataType, string dtd, long? maxLength, long? precision, long? scale)
    {
        var isUnsigned = dtd.Contains("unsigned", StringComparison.Ordinal);

        // An enum or set carries its member list, which only DTD_IDENTIFIER holds; both
        // engines spell it identically, so it is taken verbatim.
        if (dataType is "enum" or "set")
        {
            return dtd.Replace(" unsigned", string.Empty, StringComparison.Ordinal) is var bare
                && isUnsigned ? $"{bare} unsigned" : dtd;
        }

        var modifiers = new List<long>();

        if (IsCharacterType(dataType) && maxLength.HasValue)
        {
            modifiers.Add(maxLength.Value);
        }
        else if (IsDecimalType(dataType) && precision.HasValue)
        {
            modifiers.Add(precision.Value);
            modifiers.Add(scale ?? 0);
        }
        else if (dataType == "tinyint" && dtd.StartsWith("tinyint(1)", StringComparison.Ordinal))
        {
            // Both engines spell a BOOL parameter tinyint(1), and the width is meaningful
            // there — it is what distinguishes BOOL from a plain TINYINT.
            modifiers.Add(1);
        }

        return MariaDbTypeNormalizer.Normalize(dataType, modifiers, isUnsigned);
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
            // The raw COLUMN_TYPE preserves the case of enum/set literals (e.g. 'PG-13');
            // the lower-cased copy is used where case is irrelevant (type name, unsigned).
            var rawColumnType = reader.GetString("COLUMN_TYPE");
            var columnType = rawColumnType.ToLowerInvariant();
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

            // For enum/set, DATA_TYPE is the bare "enum"/"set" and COLUMN_TYPE carries the
            // value list, e.g. "enum('g','pg')". Keep the parenthesized list so it matches
            // what the parser records and can be reproduced when scripting the column.
            if (dataType is "enum" or "set")
            {
                var open = rawColumnType.IndexOf('(');
                if (open >= 0)
                {
                    typeElement.Properties.Add(new Property(
                        MariaDbPropertyNames.CollectionValues, rawColumnType[open..]));
                }
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

            // enum/set defaults are string literals too, and MySQL reports them unquoted (as
            // it does for char/varchar), so they need the same re-quoting to match the parser.
            var defaultIsStringLiteral = IsCharacterType(dataType) || dataType is "enum" or "set";

            if (MariaDbDefaultValue.FromDatabaseText(columnDefault, defaultIsStringLiteral) is { } defaultValue)
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

using System.Data.Common;
using Squill.Core;
using Squill.Dacpac;
using Squill.MariaDbParser.Syntax;
using ForeignKeyAccumulator = Squill.Core.ForeignKeyAccumulator<Squill.Provider.MariaDb.SqlName, Squill.MariaDbParser.Syntax.ReferentialAction>;

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

    /// <summary>
    /// The schema provider for the server actually connected to, resolved from its
    /// <c>VERSION()</c> during extraction. Not a constructor parameter: a few catalog forms —
    /// the time-function column defaults of issue #147 — mean different things on each engine,
    /// and the only trustworthy answer is the server being read, not what a caller believed it
    /// was connecting to.
    /// </summary>
    private MariaDbFamilyDatabaseSchemaProvider? _schemaProvider;

    // Set before any column is read; a null here would mean extraction ran out of order.
    private MariaDbFamilyDatabaseSchemaProvider SchemaProvider => _schemaProvider
        ?? throw new InvalidOperationException(
            "The target engine has not been detected yet; extraction must connect first.");

    public MariaDbDatabaseModelBuilder(IDatabase database)
    {
        _database = database;
    }

    /// <summary>
    /// Reads the server's version banner and classifies it. MariaDB always carries
    /// <c>MariaDB</c> in <c>VERSION()</c> (e.g. <c>11.4.2-MariaDB</c>); MySQL never does.
    /// </summary>
    private async Task<MariaDbFamilyDatabaseSchemaProvider> DetectSchemaProviderAsync(
        CancellationToken cancellationToken)
    {
        await using var reader = await _database.RunScriptReaderAsync(
            "SELECT VERSION();", [], cancellationToken);

        var version = await reader.ReadAsync(cancellationToken)
            ? reader.GetString(0)
            : string.Empty;

        // Shared with the deploy side rather than duplicated: the script generator classifies
        // the same banner, and if the two disagreed a build would extract as one engine and
        // script as the other (issue #211).
        return MariaDbEngineDetection.FromServerVersion(version);
    }

    // MariaDB information_schema stores bare identifiers; we store the canonical SqlName on
    // the element. This pairs the two so extraction can do both.
    // TableCollation is the table's own collation as the catalog reports it, which is the
    // default every unqualified string column in it inherits (issue #216). Carried on the ref
    // so ExtractColumnsAsync can tell a column that declared a collation from one that merely
    // inherited the table's.
    private sealed record TableRef(Element Element, string BareName, string? TableCollation = null);

    /// <summary>
    /// Records the table options that survive a round trip (issue #207), following the same
    /// omit-when-default convention the parse side uses so the two models hash-match.
    ///
    /// <para>
    /// The catalog fills every one of these in whether or not the table declared it, so an
    /// absent clause and a declared default are indistinguishable here. Each is therefore stored
    /// only when it differs from what an undeclared table would report: the schema's default
    /// collation, the engine's default engine, and an empty comment.
    /// </para>
    /// </summary>
    private static void AddTableOptions(Element element, DbDataReader reader)
    {
        // Compared against the server's default rather than recorded outright: the catalog names
        // an engine for every table, so storing it unconditionally would record one here for a
        // table whose source declared none, and the two models would stop matching.
        var engine = reader.GetStringOrNull("ENGINE");
        var defaultEngine = reader.GetStringOrNull("DEFAULT_ENGINE");

        if (engine is not null
            && !string.Equals(engine, defaultEngine, StringComparison.OrdinalIgnoreCase))
        {
            element.Properties.Add(new Property(
                MariaDbPropertyNames.Engine, ParserWorkspaceModelBuilder.CanonicalEngineName(engine)));
        }

        // Recorded only when it differs from the schema's default, which is the one comparison
        // the extractor can make. A table that declares its schema's default collation and one
        // that declares nothing are byte-identical here (measured: in a schema defaulting to
        // utf8mb4_bin, `COLLATE=utf8mb4_bin` and no COLLATE both report utf8mb4_bin), so no rule
        // could tell them apart. Treating both as "inherited" is the half of that ambiguity the
        // build can match: the parse side applies the same rule against the engine's known
        // default, so a table declaring it records nothing on either side.
        var collation = reader.GetStringOrNull("TABLE_COLLATION");
        var schemaCollation = reader.GetStringOrNull("DEFAULT_COLLATION_NAME");

        if (collation is not null
            && !string.Equals(collation, schemaCollation, StringComparison.OrdinalIgnoreCase))
        {
            element.Properties.Add(new Property(
                MariaDbPropertyNames.Collation,
                ParserWorkspaceModelBuilder.CanonicalCollationName(collation)));
        }

        // Both engines report a table with no COMMENT as the empty string rather than null, so
        // an empty one is the absent case and is not stored.
        if (reader.GetStringOrNull("TABLE_COMMENT") is { Length: > 0 } comment)
        {
            element.Properties.Add(new Property(MariaDbPropertyNames.TableComment, comment));
        }
    }

    public async Task<Model> ExtractModelAsync(CancellationToken cancellationToken = default)
    {
        var model = new Model();

        await _database.ConnectAsync(cancellationToken);

        _schemaProvider = await DetectSchemaProviderAsync(cancellationToken);

        // ENGINE, TABLE_COLLATION and TABLE_COMMENT are read alongside the name so a table's
        // options survive the round trip (issue #207). They have to be read here rather than
        // added on the parse side alone: neither side recorded them before, so modeling them in
        // only one place would make every existing table re-diff against its own database.
        //
        // The schema's default collation and the server's default engine come along so an
        // inherited option can be told from a declared one. The catalog fills both in for every
        // table whether or not it declared them, and both defaults differ between the engines
        // (measured: utf8mb4_uca1400_ai_ci on MariaDB 12 against utf8mb4_0900_ai_ci on MySQL 9),
        // so comparing against a constant would record an option for every table on one engine
        // and none on the other.
        const string sql =
            "SELECT t.TABLE_NAME, t.ENGINE, t.TABLE_COLLATION, t.TABLE_COMMENT, "
            + "s.DEFAULT_COLLATION_NAME, "
            + "(SELECT e.ENGINE FROM information_schema.ENGINES e WHERE e.SUPPORT = 'DEFAULT' "
            + "LIMIT 1) AS DEFAULT_ENGINE "
            + "FROM information_schema.TABLES t "
            + "JOIN information_schema.SCHEMATA s ON s.SCHEMA_NAME = t.TABLE_SCHEMA "
            + "WHERE t.TABLE_SCHEMA = @db AND t.TABLE_TYPE = 'BASE TABLE' ORDER BY t.TABLE_NAME;";

        var dbParam = new[] { new DatabaseParameter<string>("@db", _database.Name) };

        var tables = new List<TableRef>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, dbParam, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = reader.GetString("TABLE_NAME");

                var element = MariaDbModelFactory.CreateTable(SqlName.Object(name));

                AddTableOptions(element, reader);

                tables.Add(new TableRef(
                    element, name, reader.GetStringOrNull("TABLE_COLLATION")));
            }
        }

        // Sequences come before the tables: a column may default to NEXTVAL(seq), so the
        // sequence has to exist first, and the parser-based builder orders them the same way.
        await ExtractSequencesAsync(model, cancellationToken);

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

            // CHECK constraints come last, matching the parser-based builder's order.
            await ExtractCheckConstraintsAsync(model, table, cancellationToken);
        }

        // Views come after tables (a view selects from them) and before procedures, whose
        // bodies may in turn query a view. The Merkle hash is order-sensitive, so this
        // matches the order the parser-based builder produces.
        await ExtractViewsAsync(model, cancellationToken);

        // Routines (procedures and functions) come after views, matching the parser-based
        // builder: a routine body may reference any table, so on publish its CREATE must run
        // after the tables it uses. Procedures and functions are ordered together by name.
        await ExtractRoutinesAsync(model, cancellationToken);

        // Triggers come last, matching the parser-based builder: a trigger fires on a table
        // and its body may touch any table or view, so its CREATE must run after everything
        // else. Ordered by name so the two builders agree (the Merkle hash is order-sensitive).
        await ExtractTriggersAsync(model, cancellationToken);

        // Events come after triggers, matching the parser-based builder. An event is bound to
        // no table, but its body may touch anything, so it is created last. Ordered by name
        // so the two builders agree (the Merkle hash is order-sensitive).
        await ExtractEventsAsync(model, cancellationToken);

        return model;
    }

    /// <summary>
    /// Reads the database's sequences (issue #218).
    ///
    /// <para>
    /// Unlike every other object here, a sequence's options are not in a catalog view at all:
    /// <c>information_schema.TABLES</c> lists it with <c>TABLE_TYPE = 'SEQUENCE'</c> and
    /// nothing more, while the values live in the sequence's own backing table, which has to be
    /// selected from directly (measured). The backing type is read from the type of that
    /// table's <c>next_not_cached_value</c> column, since it governs the default bounds.
    /// </para>
    /// </summary>
    private async Task ExtractSequencesAsync(Model model, CancellationToken cancellationToken = default)
    {
        // MySQL has no sequence object, and asking for TABLE_TYPE = 'SEQUENCE' there would be
        // a well-formed query that always returns nothing. Skipped explicitly so the intent is
        // stated rather than resting on that accident.
        if (!SchemaProvider.SupportsSequences)
        {
            return;
        }

        const string sql =
            """
            SELECT t.TABLE_NAME, c.DATA_TYPE
            FROM information_schema.TABLES t
            JOIN information_schema.COLUMNS c
              ON c.TABLE_SCHEMA = t.TABLE_SCHEMA
             AND c.TABLE_NAME = t.TABLE_NAME
             AND c.COLUMN_NAME = 'next_not_cached_value'
            WHERE t.TABLE_SCHEMA = @db AND t.TABLE_TYPE = 'SEQUENCE'
            ORDER BY t.TABLE_NAME;
            """;

        var dbParam = new[] { new DatabaseParameter<string>("@db", _database.Name) };

        var sequences = new List<(string Name, string DataType)>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, dbParam, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                sequences.Add((reader.GetString("TABLE_NAME"), reader.GetString("DATA_TYPE")));
            }
        }

        foreach (var (name, dataType) in sequences)
        {
            // The options live in the sequence itself. The name cannot be parameterized here
            // because it is an identifier rather than a value, so it is quoted instead; a
            // backtick inside an identifier is escaped by doubling it.
            var optionsSql =
                $"SELECT start_value, minimum_value, maximum_value, increment, cache_size, "
                + $"cycle_option FROM `{name.Replace("`", "``")}`;";

            await using var reader = await _database.RunScriptReaderAsync(
                optionsSql, cancellationToken: cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                continue;
            }

            model.Elements.Add(MariaDbModelFactory.CreateSequence(
                name,
                dataType,
                // GetNullableInt64 coerces the boxed value, which matters twice over here:
                // the two engines disagree on the signedness of catalog integers, and
                // cycle_option is a tinyint rather than a bigint, so a fixed GetFieldValue<long>
                // would throw on it.
                reader.GetNullableInt64("start_value"),
                reader.GetNullableInt64("increment"),
                reader.GetNullableInt64("minimum_value"),
                reader.GetNullableInt64("maximum_value"),
                reader.GetNullableInt64("cache_size"),
                reader.GetNullableInt64("cycle_option") != 0));
        }
    }

    private async Task ExtractEventsAsync(Model model, CancellationToken cancellationToken = default)
    {
        // An event's identity is its name, its schedule and its body. EVENT_TYPE selects which
        // schedule columns are populated: EXECUTE_AT for a ONE TIME event, or
        // INTERVAL_VALUE/INTERVAL_FIELD plus STARTS/ENDS for a RECURRING one.
        //
        // The timestamps are read as strings in the catalog's own 'YYYY-MM-DD HH:MM:SS' shape
        // rather than as DateTime, so an extracted value is byte-comparable with the literal
        // written in source — going through DateTime would re-render it in the host's format
        // and no declared event would ever match.
        const string sql =
            """
            SELECT EVENT_NAME, EVENT_TYPE,
                   DATE_FORMAT(EXECUTE_AT, '%Y-%m-%d %H:%i:%s') AS EXECUTE_AT,
                   INTERVAL_VALUE, INTERVAL_FIELD,
                   DATE_FORMAT(STARTS, '%Y-%m-%d %H:%i:%s') AS STARTS,
                   DATE_FORMAT(ENDS, '%Y-%m-%d %H:%i:%s') AS ENDS,
                   STATUS, ON_COMPLETION, EVENT_COMMENT, EVENT_DEFINITION
            FROM information_schema.EVENTS
            WHERE EVENT_SCHEMA = @db
            ORDER BY EVENT_NAME;
            """;

        var dbParam = new[] { new DatabaseParameter<string>("@db", _database.Name) };

        var events = new List<Element>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, dbParam, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                events.Add(MariaDbModelFactory.CreateEvent(
                    reader.GetString("EVENT_NAME"),
                    reader.GetString("EVENT_TYPE").ToUpperInvariant(),
                    reader.GetString("EVENT_DEFINITION"),
                    reader.GetStringOrNull("EXECUTE_AT"),
                    NormalizeIntervalValue(reader.GetStringOrNull("INTERVAL_VALUE")),
                    reader.GetStringOrNull("INTERVAL_FIELD")?.ToUpperInvariant(),
                    reader.GetStringOrNull("STARTS"),
                    reader.GetStringOrNull("ENDS"),
                    NormalizeEventStatus(reader.GetString("STATUS")),
                    // ON_COMPLETION is reported as 'PRESERVE' or 'NOT PRESERVE'.
                    reader.GetString("ON_COMPLETION").Equals(
                        "PRESERVE", StringComparison.OrdinalIgnoreCase),
                    // Both engines report an absent comment as the empty string, which the
                    // factory omits, matching a declaration that wrote no COMMENT.
                    reader.GetStringOrNull("EVENT_COMMENT")));
            }
        }

        foreach (var element in events)
        {
            model.Elements.Add(element);
        }
    }

    // A compound interval (EVERY '2:3' DAY_HOUR) is stored by both engines with its quotes
    // included — INTERVAL_VALUE literally reads '2 3', apostrophes and all — while a simple
    // count (EVERY 1 DAY) is stored bare. The quotes are stripped so an extracted value
    // matches the unquoted form the parser records.
    private static string? NormalizeIntervalValue(string? intervalValue)
        => intervalValue is { Length: >= 2 } value && value[0] == '\'' && value[^1] == '\''
            ? value[1..^1]
            : intervalValue;

    // MySQL renamed the DISABLE ON SLAVE status to REPLICA_SIDE_DISABLED, while MariaDB still
    // reports SLAVESIDE_DISABLED for the identical clause. Normalizing onto the MariaDB
    // spelling — the one the parser records — lets a single declaration match on both engines.
    private static string NormalizeEventStatus(string status)
        => status.Equals("REPLICA_SIDE_DISABLED", StringComparison.OrdinalIgnoreCase)
            ? "SLAVESIDE_DISABLED"
            : status.ToUpperInvariant();

    private async Task ExtractViewsAsync(Model model, CancellationToken cancellationToken = default)
    {
        // A view's columns are read from information_schema.COLUMNS in ordinal order, which
        // is the shape both engines report for the deployed view.
        //
        // The view's own query (VIEW_DEFINITION) is deliberately NOT read. MariaDB and
        // MySQL each rewrite the query when they store it — and differently from each other
        // — so it could never match the declared source. A view's modeled identity is its
        // name and column list instead; see MariaDbModelFactory.CreateView.
        // Issue #208: CHECK_OPTION and SECURITY_TYPE decide how the view executes and are
        // reported faithfully by both engines, so unlike the query they can be modeled.
        //
        // ALGORITHM is selected only where it exists: MariaDB's VIEWS has that column, MySQL's
        // has none at all (measured), so naming it unconditionally would be an unknown-column
        // error there, the same shape as the EXPRESSION column above.
        var algorithmColumn = SchemaProvider.ReportsViewAlgorithm ? ",\n       v.ALGORITHM" : string.Empty;

        var sql =
            $"""
            SELECT v.TABLE_NAME, v.CHECK_OPTION, v.SECURITY_TYPE{algorithmColumn},
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

                // NONE means the view has no CHECK OPTION; the parser records null for the
                // same state, so it must not become a property here.
                var checkOption = reader.GetStringOrNull("CHECK_OPTION") is { } declared
                    && !declared.Equals("NONE", StringComparison.OrdinalIgnoreCase)
                        ? declared.ToUpperInvariant()
                        : null;

                // UNDEFINED is the default and records nothing, matching the parse side.
                var algorithm = SchemaProvider.ReportsViewAlgorithm
                    && reader.GetStringOrNull("ALGORITHM") is { } reported
                    && !reported.Equals("UNDEFINED", StringComparison.OrdinalIgnoreCase)
                        ? reported.ToUpperInvariant()
                        : null;

                views.Add(MariaDbModelFactory.CreateView(
                    SqlName.Object(name),
                    columnNames.Length == 0 ? [] : columnNames.Split(ViewColumnSeparator),
                    // The database's own query text is never modeled — see above.
                    definition: null,
                    checkOption,
                    // DEFINER is the default and records nothing, so only INVOKER is stored.
                    isSecurityInvoker: string.Equals(
                        reader.GetStringOrNull("SECURITY_TYPE"), "INVOKER",
                        StringComparison.OrdinalIgnoreCase),
                    algorithm));
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

    private async Task ExtractTriggersAsync(Model model, CancellationToken cancellationToken = default)
    {
        // A trigger's identity is its name, the table it fires on (EVENT_OBJECT_TABLE), its
        // timing (ACTION_TIMING = BEFORE/AFTER) and event (EVENT_MANIPULATION =
        // INSERT/UPDATE/DELETE), and its body (ACTION_STATEMENT, returned verbatim by both
        // engines). Ordered by name so the parser-based builder can adopt the same order — the
        // catalog has no notion of declaration order and the Merkle hash is order-sensitive.
        const string sql =
            """
            SELECT TRIGGER_NAME, EVENT_OBJECT_TABLE, ACTION_TIMING, EVENT_MANIPULATION,
                   ACTION_STATEMENT
            FROM information_schema.TRIGGERS
            WHERE TRIGGER_SCHEMA = @db
            ORDER BY TRIGGER_NAME;
            """;

        var dbParam = new[] { new DatabaseParameter<string>("@db", _database.Name) };

        var triggers = new List<Element>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, dbParam, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                triggers.Add(MariaDbModelFactory.CreateTrigger(
                    SqlName.Object(reader.GetString("EVENT_OBJECT_TABLE")),
                    reader.GetString("TRIGGER_NAME"),
                    reader.GetString("ACTION_TIMING").ToUpperInvariant(),
                    reader.GetString("EVENT_MANIPULATION").ToUpperInvariant(),
                    reader.GetString("ACTION_STATEMENT")));
            }
        }

        foreach (var trigger in triggers)
        {
            model.Elements.Add(trigger);
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

        if (IsLengthType(dataType) && maxLength.HasValue)
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
                -- The fractional-seconds precision of a datetime/timestamp/time column
                -- (issue #144). Reported here rather than in NUMERIC_PRECISION.
                DATETIME_PRECISION,
                EXTRA,
                -- A column's own COMMENT and COLLATE (issue #216). COLLATION_NAME is populated
                -- for every string column whether or not one was declared, so a value equal to
                -- the engine's default is dropped below to match what the parser records.
                COLUMN_COMMENT,
                COLLATION_NAME,
                COLUMN_DEFAULT,
                -- A generated (computed) column (issue #120). Both engines report the
                -- rewritten expression here and mark the storage kind in EXTRA as
                -- "STORED GENERATED" or "VIRTUAL GENERATED".
                GENERATION_EXPRESSION
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
            var datetimePrecision = reader.GetNullableInt64("DATETIME_PRECISION") ?? 0;
            var extra = reader.GetString("EXTRA");
            var isAutoIncrement = extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase);
            // Both engines report an INVISIBLE column by putting the keyword in EXTRA, alongside
            // whatever else it carries (issue #216).
            var isInvisible = extra.Contains("INVISIBLE", StringComparison.OrdinalIgnoreCase);
            var columnComment = reader.GetStringOrNull("COLUMN_COMMENT");
            var collation = reader.GetStringOrNull("COLLATION_NAME");
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

            // A character/binary type carries its length; a numeric(p,s) type carries
            // precision and scale. These mirror what the parser builder records so both
            // sides hash-match.
            if (IsLengthType(dataType) && maxLength.HasValue)
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
            else if (MariaDbTypeCategories.IsVectorType(dataType)
                     && VectorDimension(columnType) is { } dimension)
            {
                // A vector's dimension (issue #217), read back out of COLUMN_TYPE. It is not
                // taken from CHARACTER_MAXIMUM_LENGTH, which reports the storage size in bytes
                // (measured: a `VECTOR(3)` column reports 12) and would not match the declared
                // dimension the parser side records.
                typeElement.Properties.Add(new Property(MariaDbPropertyNames.Length, dimension));
            }
            else if (MariaDbTypeCategories.IsTemporalPrecisionType(dataType)
                     && datetimePrecision > 0)
            {
                // A fractional-seconds precision, e.g. datetime(3) (issue #144). Reported in
                // DATETIME_PRECISION, not NUMERIC_PRECISION, but stored under the same
                // Precision property the parser builder uses — these types never carry a
                // decimal precision. Omitted when 0, matching the parser side: both engines
                // report a `datetime(0)` column as plain `datetime`.
                typeElement.Properties.Add(
                    new Property(MariaDbPropertyNames.Precision, (long)datetimePrecision));
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

            // A column that declared no COMMENT reports an empty string rather than NULL, so
            // only a non-empty one is recorded, matching the parser side (issue #216).
            if (!string.IsNullOrEmpty(columnComment))
            {
                column.Properties.Add(
                    new Property(MariaDbPropertyNames.ColumnComment, columnComment));
            }

            if (isInvisible)
            {
                column.Properties.Add(new Property(MariaDbPropertyNames.IsInvisible, true));
            }

            // COLLATION_NAME is populated for every string column, declared or not, so one equal
            // to this target's default is indistinguishable from an absent clause and is dropped.
            // The parser side drops the same value, so the two models agree either way.
            // The value to compare against is the table's own collation, not the engine's: an
            // unqualified column inherits the table's, so in a `COLLATE=latin1_general_ci`
            // table every column reports latin1_general_ci whether or not it declared one
            // (measured). Comparing against the engine default instead would record a
            // collation on every column of such a table, which the parse side never records.
            var inheritedCollation = table.TableCollation ?? SchemaProvider.DefaultCollation;

            if (collation is not null
                && !string.Equals(collation, inheritedCollation, StringComparison.OrdinalIgnoreCase))
            {
                column.Properties.Add(new Property(
                    MariaDbPropertyNames.Collation,
                    ParserWorkspaceModelBuilder.CanonicalCollationName(collation)));
            }

            var columnDefault = reader.IsDBNull(reader.GetOrdinal("COLUMN_DEFAULT"))
                ? null
                : reader.GetString("COLUMN_DEFAULT");

            // enum/set defaults are string literals too, and MySQL reports them unquoted (as
            // it does for char/varchar), so they need the same re-quoting to match the parser.
            var defaultIsStringLiteral = IsCharacterType(dataType) || dataType is "enum" or "set";

            if (MariaDbDefaultValue.FromDatabaseText(columnDefault, SchemaProvider, defaultIsStringLiteral)
                is { } defaultValue)
            {
                column.Properties.Add(new Property(MariaDbPropertyNames.DefaultValue, defaultValue));
            }

            // ON UPDATE CURRENT_TIMESTAMP (issue #124). Both engines report it in EXTRA but
            // spell it differently — MySQL "on update CURRENT_TIMESTAMP", MariaDB
            // "on update current_timestamp()". The fractional-seconds form is modeled too
            // (issue #144): "on update current_timestamp(3)" canonicalizes with its precision
            // intact, matching the parser side. Emitted after the default to match the parser
            // builder's property order (the hash is order-sensitive).
            if (MariaDbDefaultValue.CanonicalOnUpdate(OnUpdateToken(extra), SchemaProvider) is { } onUpdate)
            {
                column.Properties.Add(
                    new Property(MariaDbPropertyNames.OnUpdateCurrentTimestamp, onUpdate));
            }

            // A generated column (issue #120). EXTRA carries "STORED GENERATED" or
            // "VIRTUAL GENERATED"; GENERATION_EXPRESSION is empty for an ordinary column.
            //
            // Match those two forms specifically rather than a bare "GENERATED": MySQL also
            // reports "DEFAULT_GENERATED" in EXTRA for an ordinary column that merely has a
            // non-constant default such as CURRENT_TIMESTAMP, which would otherwise be
            // misread as a generated column and given empty generation properties the parsed
            // model does not have (issue #124).
            if (extra.Contains("STORED GENERATED", StringComparison.OrdinalIgnoreCase)
                || extra.Contains("VIRTUAL GENERATED", StringComparison.OrdinalIgnoreCase))
            {
                var generationExpression = reader.IsDBNull(reader.GetOrdinal("GENERATION_EXPRESSION"))
                    ? null
                    : reader.GetString("GENERATION_EXPRESSION");

                MariaDbModelFactory.AddGeneratedColumnProperties(column, generationExpression,
                    isStored: extra.Contains("STORED", StringComparison.OrdinalIgnoreCase));
            }

            columns.Entries.Add(column);
        }
    }

    /// <summary>
    /// The function token of an <c>ON UPDATE</c> clause reported in
    /// <c>information_schema.COLUMNS.EXTRA</c>, or <c>null</c> if there is none. EXTRA may
    /// carry other flags alongside it (MySQL prefixes <c>DEFAULT_GENERATED</c>), so the token
    /// is taken as the remainder after "on update".
    /// </summary>
    private static string? OnUpdateToken(string extra)
    {
        var index = extra.IndexOf("on update", StringComparison.OrdinalIgnoreCase);

        return index < 0 ? null : extra[(index + "on update".Length)..].Trim();
    }

    private async Task ExtractPrimaryKeyAsync(Model model, TableRef table, CancellationToken cancellationToken = default)
    {
        var tableSqlName = SqlName.Object(table.BareName);

        // MariaDB always names the primary key constraint 'PRIMARY'. Its columns come from
        // STATISTICS (INDEX_NAME = 'PRIMARY'), ordered by SEQ_IN_INDEX.
        // SUB_PART is the declared prefix length, NULL for a whole-column key (issue #161). It
        // must be read here as well as in the source mapper: reading it on only one side would
        // turn a silently-wrong index into one that re-diffs on every deploy.
        const string sql = """
            SELECT COLUMN_NAME, SUB_PART
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
                    tableSqlName.Child(reader.GetString("COLUMN_NAME")),
                    PrefixLength: (int?)reader.GetNullableInt64("SUB_PART")));
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
        // SUB_PART is the declared prefix length, NULL for a whole-column key. EXPRESSION is the
        // functional key's text — MySQL-only, and MariaDB's STATISTICS has no such column, so
        // naming it there is an unknown-column error; the capability decides (issue #161).
        var expressionColumn = SchemaProvider.SupportsFunctionalIndexKeys ? ",\n    EXPRESSION" : string.Empty;

        // Index visibility is spelled and reported differently by the two engines (issue #211):
        // MySQL has IS_VISIBLE, MariaDB has IGNORED, and neither has the other's column, so
        // naming the wrong one is an unknown-column error, exactly as with EXPRESSION above.
        // Both are aliased to one name so the reader below does not branch a second time.
        var visibilityColumn = SchemaProvider.IndexVisibility switch
        {
            IndexVisibilityStyle.Invisible => ",\n    IS_VISIBLE AS visibility",
            IndexVisibilityStyle.Ignored => ",\n    IGNORED AS visibility",
            _ => string.Empty,
        };

        var sql = $"""
            SELECT
                INDEX_NAME,
                NON_UNIQUE,
                INDEX_TYPE,
                SEQ_IN_INDEX,
                COLUMN_NAME,
                COLLATION,
                INDEX_COMMENT,
                SUB_PART{expressionColumn}{visibilityColumn}
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = @db AND TABLE_NAME = @name AND INDEX_NAME <> 'PRIMARY'
            ORDER BY INDEX_NAME, SEQ_IN_INDEX;
            """;

        var parameters = new[]
        {
            new DatabaseParameter<string>("@db", _database.Name),
            new DatabaseParameter<string>("@name", table.BareName),
        };

        var indexRows = new Dictionary<string,
            (bool IsUnique, string Method, string? Comment, bool IsHidden,
             List<MariaDbModelFactory.IndexedColumn> Columns)>();
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

                    // Both engines report INDEX_COMMENT as the empty string, not NULL, for an
                    // index that declared none, so empty maps to absent, matching the source
                    // builder's omit-when-default handling (issue #211).
                    var comment = reader.GetStringOrNull("INDEX_COMMENT");

                    // The two engines report visibility in differently-named columns holding
                    // opposite senses: MySQL's IS_VISIBLE is 'YES' when the optimizer *uses* the
                    // index, MariaDB's IGNORED is 'YES' when it does not. Both are folded into
                    // the one hidden-from-optimizer flag the model stores.
                    var isHidden = SchemaProvider.IndexVisibility switch
                    {
                        IndexVisibilityStyle.Invisible =>
                            reader.GetStringOrNull("visibility") == "NO",
                        IndexVisibilityStyle.Ignored =>
                            reader.GetStringOrNull("visibility") == "YES",
                        _ => false,
                    };

                    entry = (nonUnique == 0, indexType,
                        string.IsNullOrEmpty(comment) ? null : comment, isHidden, new());
                    indexRows.Add(indexName, entry);
                    order.Add(indexName);
                }

                // COLLATION 'D' marks a descending column; 'A' ascending; NULL unordered.
                bool? isAscending = reader.IsDBNull(reader.GetOrdinal("COLLATION"))
                    ? null
                    : reader.GetString("COLLATION") == "A";

                // A functional key reports COLUMN_NAME NULL and its text in EXPRESSION, so it
                // takes the index's own name for identity — the same shape the source builder
                // gives it, which is what lets the two sides hash-match (issue #161).
                var keyExpression = SchemaProvider.SupportsFunctionalIndexKeys
                    ? reader.GetStringOrNull("EXPRESSION")
                    : null;

                entry.Columns.Add(keyExpression is not null
                    ? new MariaDbModelFactory.IndexedColumn(
                        SqlName.Object(indexName), isAscending, KeyExpression: keyExpression)
                    : new MariaDbModelFactory.IndexedColumn(
                        tableSqlName.Child(reader.GetString("COLUMN_NAME")),
                        isAscending,
                        PrefixLength: (int?)reader.GetNullableInt64("SUB_PART")));
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

            var (isUnique, method, comment, isHidden, columns) = indexRows[indexName];

            // INDEX_TYPE reports FULLTEXT/SPATIAL in the same slot as the BTREE/HASH access
            // method, but they are index *kinds*, not methods: `USING FULLTEXT` is a syntax
            // error on both engines, so scripting one as a method would emit invalid DDL. Split
            // them apart here (issue #146).
            var isSpecialKind = method is "FULLTEXT" or "SPATIAL";

            model.Elements.Add(MariaDbModelFactory.CreateIndex(
                SqlName.Object(indexName), tableSqlName, isUnique,
                isSpecialKind ? null : method, columns,
                indexKind: isSpecialKind ? method : null,
                comment: comment,
                isHiddenFromOptimizer: isHidden));
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

    // Extracts the table's CHECK constraints (issue #120). Both engines expose them through
    // information_schema.CHECK_CONSTRAINTS, which reports the predicate as the engine
    // rewrote it — which is why the predicate does not take part in comparison.
    //
    // MariaDB also lists a column's `NOT NULL` here in some versions; those never carry an
    // explicit name in the source, and an unnamed CHECK is a build error, so a constraint
    // whose predicate is just a NOT NULL test is skipped to avoid a phantom re-diff.
    private async Task ExtractCheckConstraintsAsync(Model model, TableRef table,
        CancellationToken cancellationToken = default)
    {
        var tableSqlName = SqlName.Object(table.BareName);

        // CHECK_CONSTRAINTS is joined to TABLE_CONSTRAINTS to find the owning table: MariaDB
        // exposes a TABLE_NAME column on CHECK_CONSTRAINTS but MySQL does not, and this
        // provider serves both. TABLE_CONSTRAINTS carries TABLE_NAME on both engines.
        const string sql = """
            SELECT cc.CONSTRAINT_NAME, cc.CHECK_CLAUSE
            FROM information_schema.CHECK_CONSTRAINTS cc
            JOIN information_schema.TABLE_CONSTRAINTS tc
              ON tc.CONSTRAINT_SCHEMA = cc.CONSTRAINT_SCHEMA
             AND tc.CONSTRAINT_NAME = cc.CONSTRAINT_NAME
             AND tc.CONSTRAINT_TYPE = 'CHECK'
            WHERE cc.CONSTRAINT_SCHEMA = @db AND tc.TABLE_NAME = @name
            ORDER BY cc.CONSTRAINT_NAME;
            """;

        var parameters = new[]
        {
            new DatabaseParameter<string>("@db", _database.Name),
            new DatabaseParameter<string>("@name", table.BareName),
        };

        var checks = new List<(string Name, string Clause)>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                checks.Add((reader.GetString("CONSTRAINT_NAME"), reader.GetString("CHECK_CLAUSE")));
            }
        }

        foreach (var (name, clause) in checks)
        {
            if (clause.EndsWith("is not null", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            model.Elements.Add(MariaDbModelFactory.CreateCheckConstraint(
                tableSqlName.Sibling(name), tableSqlName, clause));
        }
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

    // Character types, whose column defaults are string literals that MySQL reports unquoted.
    private static bool IsCharacterType(string dataType)
        => MariaDbTypeCategories.IsCharacterType(dataType);

    // Types whose single modifier is a length: character types and binary types. The length
    // must be carried through so a `varbinary`, which requires an explicit length, can be
    // recreated (issue #97).
    private static bool IsLengthType(string dataType)
        => IsCharacterType(dataType) || dataType is "binary" or "varbinary";

    private static bool IsDecimalType(string dataType)
        => MariaDbTypeCategories.IsDecimalType(dataType);

    /// <summary>
    /// The declared dimension of a <c>vector(n)</c> column, parsed out of its
    /// <c>COLUMN_TYPE</c> (issue #217), or <c>null</c> if it carries none.
    /// </summary>
    private static int? VectorDimension(string columnType)
    {
        var open = columnType.IndexOf('(');
        var close = columnType.IndexOf(')');

        return open >= 0 && close > open
               && int.TryParse(columnType[(open + 1)..close], out var dimension)
            ? dimension
            : null;
    }
}

using System.Data;
using Squill.Core;
using Squill.PostgresParser.Syntax;
using ForeignKeyAccumulator = Squill.Core.ForeignKeyAccumulator<Squill.Provider.Postgres.SqlName, Squill.PostgresParser.Syntax.ReferentialAction>;

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

        // BASE TABLE excludes views, which information_schema.tables also lists: a view is
        // extracted as its own SqlView element (issue #42), and without this filter every
        // view would additionally be modeled as a table.
        const string sql =
            "SELECT * FROM information_schema.tables WHERE table_type = 'BASE TABLE';";

        // Schemas and extensions are extracted first so they lead the model's element
        // order. A table lives in a schema and may use a type provided by an extension
        // (e.g. pgvector's vector), so on publish the CREATE SCHEMA / CREATE EXTENSION must
        // run before the CREATE TABLE that depends on them.
        await ExtractSchemasAsync(model, cancellationToken);
        await ExtractExtensionsAsync(model, cancellationToken);

        // Enum types and domains (issue #75) are user-defined types a column may be typed
        // as, so they must precede the tables in the model — both for a hash-matching
        // element order and so CREATE TYPE / CREATE DOMAIN run before the CREATE TABLE.
        await ExtractEnumTypesAsync(model, cancellationToken);
        await ExtractDomainsAsync(model, cancellationToken);

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

                tables.Add(new TableRef(element, schema, name));
            }
        }

        // Emit each table immediately followed by its dependents (primary key, indexes,
        // foreign keys), so the element order matches the parser-based builder, which yields
        // a table and its dependents together. The Merkle hash is order-sensitive, so the
        // two builders must agree on ordering for a parsed model to hash-match an extracted
        // one — adding every table first and the dependents afterwards diverges as soon as
        // there is more than one table (issue #65).
        foreach (var table in tables)
        {
            model.Elements.Add(table.Element);

            await ExtractColumnsAsync(table, cancellationToken);
            await ExtractPrimaryKeyAsync(model, table, cancellationToken);

            // Unique constraints sit between the primary key and the foreign keys, matching
            // the order the parser-based builder emits them in.
            await ExtractUniqueConstraintsAsync(model, table, cancellationToken);

            // CHECK constraints follow the unique constraints and precede the foreign keys,
            // again matching the parser-based builder's order (issue #120).
            await ExtractCheckConstraintsAsync(model, table, cancellationToken);

            // Foreign keys precede indexes, matching the parser: a table's constraints are
            // written in its CREATE TABLE, while a standalone index comes from a separate
            // CREATE INDEX statement that follows it.
            await ExtractForeignKeysAsync(model, table, cancellationToken);
            await ExtractIndexesAsync(model, table, cancellationToken);
        }

        // Views come after tables (a view selects from them) and before procedures, whose
        // bodies may in turn query a view. The Merkle hash is order-sensitive, so this
        // order must match the one the parser-based builder produces.
        await ExtractViewsAsync(model, cancellationToken);

        // Functions then procedures come last: a routine body may reference any table, so on
        // publish its CREATE must run after the tables it reads or writes exist. Functions
        // are extracted before procedures, matching the parser builder's MoveRoutinesToEnd,
        // so a parsed model hash-matches an extracted one (the Merkle hash is order-sensitive).
        await ExtractFunctionsAsync(model, cancellationToken);
        await ExtractProceduresAsync(model, cancellationToken);

        // Aggregates come last of all: one references a state function (SFUNC), so on publish
        // its CREATE must run after that function's. This matches MoveRoutinesToEnd, which
        // orders aggregates after functions and procedures (issue #82).
        await ExtractAggregatesAsync(model, cancellationToken);

        // Triggers come after everything: one depends on both its table and the function it
        // runs. This matches the parser builder's MoveTriggersToEnd (issue #83).
        await ExtractTriggersAsync(model, cancellationToken);

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
        // The installed version is recorded so a source that pins WITH VERSION can be
        // diffed to an ALTER EXTENSION ... UPDATE. A source that pins no version leaves it
        // unmanaged; SchemaCompare backfills the installed version onto the source before
        // hashing so an unpinned extension still hash-matches (see the dependency
        // analyzer's comparison normalization).
        const string sql =
            "SELECT extname, extversion FROM pg_extension WHERE extname <> 'plpgsql' ORDER BY extname;";

        var extensions = new List<(string Name, string Version)>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, cancellationToken: cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                extensions.Add((reader.GetString("extname"), reader.GetString("extversion")));
            }
        }

        foreach (var (extensionName, extensionVersion) in extensions)
        {
            model.Elements.Add(
                PostgresModelFactory.CreateExtension(SqlName.Object(extensionName), extensionVersion));
        }
    }

    private async Task ExtractEnumTypesAsync(Model model, CancellationToken cancellationToken = default)
    {
        // Every enum type (pg_type.typtype = 'e') with its labels in significant sort order
        // (pg_enum.enumsortorder). System schemas are excluded, and a type owned by an
        // extension is skipped (created by CREATE EXTENSION, not declared in the project),
        // mirroring how extensions' own objects are handled elsewhere. Ordered by schema then
        // name (COLLATE "C" for byte-wise ordering) to match the parser builder's ordering.
        const string sql =
            """
            SELECT n.nspname AS schema_name, t.typname AS type_name, e.enumlabel AS label
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            JOIN pg_enum e ON e.enumtypid = t.oid
            WHERE t.typtype = 'e'
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND NOT EXISTS (SELECT 1 FROM pg_depend d
                              WHERE d.objid = t.oid AND d.deptype = 'e')
            ORDER BY n.nspname COLLATE "C", t.typname COLLATE "C", e.enumsortorder;
            """;

        // Accumulate labels per (schema, type) preserving encounter order (the query is
        // ordered by enumsortorder), then build one element per enum type.
        var enums = new List<(string Schema, string Name, List<string> Labels)>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, cancellationToken: cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString("schema_name");
                var name = reader.GetString("type_name");
                var label = reader.GetString("label");

                var existing = enums.Count > 0 && enums[^1].Schema == schema && enums[^1].Name == name
                    ? enums[^1]
                    : default;

                if (existing.Labels is not null)
                {
                    existing.Labels.Add(label);
                }
                else
                {
                    enums.Add((schema, name, new List<string> { label }));
                }
            }
        }

        foreach (var (schema, name, labels) in enums)
        {
            model.Elements.Add(PostgresModelFactory.CreateEnumType(SqlName.Object(name), schema, labels));
        }
    }

    private async Task ExtractDomainsAsync(Model model, CancellationToken cancellationToken = default)
    {
        // Every domain (pg_type.typtype = 'd') with its base type rendered by format_type()
        // and its CHECK constraint definition (pg_get_constraintdef). A domain may have no
        // CHECK, so the constraint join is a LEFT JOIN. System schemas and extension-owned
        // domains are excluded as for enums. The CHECK text is carried for scripting only
        // (it does not participate in the hash — PostgreSQL rewrites the predicate).
        const string sql =
            """
            SELECT n.nspname AS schema_name,
                   t.typname AS domain_name,
                   format_type(t.typbasetype, t.typtypmod) AS base_type,
                   pg_get_constraintdef(c.oid) AS check_def
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            LEFT JOIN pg_constraint c ON c.contypid = t.oid AND c.contype = 'c'
            WHERE t.typtype = 'd'
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND NOT EXISTS (SELECT 1 FROM pg_depend d
                              WHERE d.objid = t.oid AND d.deptype = 'e')
            ORDER BY n.nspname COLLATE "C", t.typname COLLATE "C";
            """;

        var domains = new List<(string Schema, string Name, string BaseType, string? Check)>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, cancellationToken: cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString("schema_name");
                var name = reader.GetString("domain_name");
                var baseType = reader.GetString("base_type");
                var check = reader.IsDBNull(reader.GetOrdinal("check_def"))
                    ? null
                    : NormalizeCheckDefinition(reader.GetString("check_def"));

                domains.Add((schema, name, baseType, check));
            }
        }

        foreach (var (schema, name, baseType, check) in domains)
        {
            var typeSpecifier = MakeTypeSpecifierElement(baseType);

            model.Elements.Add(
                PostgresModelFactory.CreateDomain(SqlName.Object(name), schema, typeSpecifier, check));
        }
    }

    // pg_get_constraintdef renders a domain CHECK as `CHECK (<predicate>)`; the model and the
    // script generator carry just the predicate (the CREATE DOMAIN emitter adds the `CHECK (`
    // wrapper), so strip the leading `CHECK ` keyword and the single wrapping parentheses
    // pg_get_constraintdef always adds. e.g. `CHECK (((VALUE >= 1901) AND (VALUE <= 2155)))`
    // becomes `((VALUE >= 1901) AND (VALUE <= 2155))`.
    private static string NormalizeCheckDefinition(string constraintDef)
    {
        var text = constraintDef.Trim();

        const string prefix = "CHECK ";
        if (text.StartsWith(prefix, StringComparison.Ordinal))
        {
            text = text[prefix.Length..].Trim();
        }

        // Remove exactly one balanced pair of outer parentheses (the wrapper), leaving any
        // inner parenthesization the predicate itself carries.
        if (text.Length >= 2 && text[0] == '(' && text[^1] == ')')
        {
            text = text[1..^1].Trim();
        }

        return text;
    }

    // A SqlTypeSpecifier element wrapping a single Type reference by canonical name — used for
    // a domain's base type, mirroring the parser builder's MakeTypeSpecifierElement.
    private static Element MakeTypeSpecifierElement(string typeName) =>
        new(PostgresElementTypes.SqlTypeSpecifier)
        {
            Relationships =
            {
                new Relationship(PostgresRelationshipNames.Type)
                {
                    new Reference(typeName) { ExternalSource = "BuiltIns" },
                },
            },
        };

    private async Task ExtractViewsAsync(Model model, CancellationToken cancellationToken = default)
    {
        // relkind = 'v' selects ordinary views; a materialized view ('m') is a different
        // object type with its own storage and is not modeled.
        //
        // A view owned by an extension is skipped for the same reason an extension's
        // procedures are: it is created by CREATE EXTENSION, not declared in the project.
        //
        // Note that the view's query is deliberately NOT read. PostgreSQL rewrites a view's
        // definition when it stores it, so pg_get_viewdef returns reformatted SQL that
        // could never match the declared source — reading it would make every view differ
        // on every deploy. A view's modeled identity is its name and column list instead;
        // see PostgresModelFactory.CreateView.
        const string sql =
            """
            SELECT n.nspname AS schema_name,
                   c.relname AS view_name,
                   COALESCE((
                       SELECT string_agg(a.attname, chr(30) ORDER BY a.attnum)
                       FROM pg_attribute a
                       WHERE a.attrelid = c.oid
                         AND a.attnum > 0
                         AND NOT a.attisdropped), '') AS column_names
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = 'v'
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND NOT EXISTS (
                  SELECT 1 FROM pg_depend d
                  WHERE d.objid = c.oid AND d.deptype = 'e')
            -- The C collation sorts byte-wise, matching the ordinal ordering the
            -- parser-based builder applies.
            ORDER BY n.nspname COLLATE "C", c.relname COLLATE "C";
            """;

        var views = new List<Element>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, cancellationToken: cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString("schema_name");
                var name = reader.GetString("view_name");
                var columnNames = reader.GetString("column_names");

                views.Add(PostgresModelFactory.CreateView(
                    SqlName.Object(schema, name),
                    schema,
                    columnNames.Length == 0
                        ? []
                        : columnNames.Split(ViewColumnSeparator),
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
    private const char ViewColumnSeparator = '';

    private async Task ExtractFunctionsAsync(Model model, CancellationToken cancellationToken = default)
    {
        // prokind = 'f' selects plain functions (as opposed to procedures 'p', aggregates 'a'
        // and window functions 'w'). Mirrors ExtractProceduresAsync, adding the return type
        // (format_type(prorettype)), set-returning flag (proretset), volatility (provolatile:
        // i=IMMUTABLE, s=STABLE, v=VOLATILE) and strictness (proisstrict). Extension-owned
        // functions are skipped as for procedures.
        const string sql =
            """
            WITH routine AS (
                SELECT p.oid,
                       n.nspname AS schema_name,
                       p.proname AS routine_name,
                       l.lanname AS language_name,
                       p.prosrc AS body,
                       p.prosecdef AS is_security_definer,
                       p.proretset AS returns_set,
                       format_type(p.prorettype, NULL) AS return_type,
                       CASE p.provolatile WHEN 'i' THEN 'IMMUTABLE'
                                          WHEN 's' THEN 'STABLE'
                                          ELSE 'VOLATILE' END AS volatility,
                       p.proisstrict AS is_strict,
                       COALESCE(
                           p.proallargtypes,
                           ARRAY(SELECT t FROM unnest(p.proargtypes) t)) AS all_arg_types,
                       p.proargmodes AS arg_modes,
                       p.proargnames AS arg_names,
                       p.proargtypes AS identity_arg_types
                FROM pg_proc p
                JOIN pg_namespace n ON n.oid = p.pronamespace
                JOIN pg_language l ON l.oid = p.prolang
                WHERE p.prokind = 'f'
                  AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                  AND NOT EXISTS (
                      SELECT 1 FROM pg_depend d
                      WHERE d.objid = p.oid AND d.deptype = 'e')
            )
            SELECT * FROM (
            SELECT r.schema_name,
                   r.routine_name,
                   r.language_name,
                   r.body,
                   r.is_security_definer,
                   r.returns_set,
                   r.return_type,
                   r.volatility,
                   r.is_strict,
                   COALESCE((
                       SELECT string_agg(format_type(t, NULL), ',' ORDER BY o)
                       FROM unnest(r.identity_arg_types) WITH ORDINALITY AS a(t, o)), '')
                       AS argument_types,
                   COALESCE((
                       SELECT string_agg(
                           CASE COALESCE(r.arg_modes[i], 'i')
                               WHEN 'i' THEN 'IN'
                               WHEN 'o' THEN 'OUT'
                               WHEN 'b' THEN 'INOUT'
                               WHEN 'v' THEN 'VARIADIC'
                               WHEN 't' THEN 'TABLE'
                           END
                           || chr(31) || COALESCE(r.arg_names[i], '')
                           || chr(31) || format_type(r.all_arg_types[i], NULL),
                           chr(30) ORDER BY i)
                       FROM generate_subscripts(r.all_arg_types, 1) i), '')
                       AS arguments
            FROM routine r
            ) p
            ORDER BY p.schema_name COLLATE "C",
                     p.routine_name COLLATE "C",
                     p.argument_types COLLATE "C";
            """;

        var functions = new List<Element>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, cancellationToken: cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                functions.Add(PostgresModelFactory.CreateFunction(
                    reader.GetString("schema_name"),
                    reader.GetString("routine_name"),
                    reader.GetString("argument_types"),
                    reader.GetString("return_type"),
                    reader.GetBoolean("returns_set"),
                    reader.GetString("language_name"),
                    reader.GetString("body"),
                    ParseArguments(reader.GetString("arguments")),
                    reader.GetString("volatility"),
                    reader.GetBoolean("is_strict"),
                    reader.GetBoolean("is_security_definer")));
            }
        }

        foreach (var function in functions)
        {
            model.Elements.Add(function);
        }
    }

    private async Task ExtractProceduresAsync(Model model, CancellationToken cancellationToken = default)
    {
        // prokind = 'p' selects procedures (as opposed to functions, aggregates and window
        // functions), which are the only routines Squill models today.
        //
        // A procedure owned by an extension is skipped: it is created by CREATE EXTENSION,
        // not declared in the project, so including it would stop a parsed model from
        // hash-matching the extracted one — the same reasoning that excludes plpgsql from
        // the extension list.
        //
        // Argument types come from proargtypes (IN/INOUT only), which is exactly what
        // determines a procedure's identity, and the full parameter list is rebuilt from
        // proallargtypes so modes and names survive. format_type() renders the canonical
        // type name without modifiers, matching what the parser-based builder normalizes to.
        const string sql =
            """
            WITH routine AS (
                SELECT p.oid,
                       n.nspname AS schema_name,
                       p.proname AS routine_name,
                       l.lanname AS language_name,
                       p.prosrc AS body,
                       p.prosecdef AS is_security_definer,
                       -- proargtypes is an oidvector, which is 0-based; proargnames and
                       -- proargmodes are 1-based arrays. Rebuilding it through unnest
                       -- yields a 1-based array so the three line up — casting it
                       -- directly would offset every name by one.
                       COALESCE(
                           p.proallargtypes,
                           ARRAY(SELECT t FROM unnest(p.proargtypes) t)) AS all_arg_types,
                       p.proargmodes AS arg_modes,
                       p.proargnames AS arg_names,
                       p.proargtypes AS identity_arg_types
                FROM pg_proc p
                JOIN pg_namespace n ON n.oid = p.pronamespace
                JOIN pg_language l ON l.oid = p.prolang
                WHERE p.prokind = 'p'
                  AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                  AND NOT EXISTS (
                      SELECT 1 FROM pg_depend d
                      WHERE d.objid = p.oid AND d.deptype = 'e')
            )
            SELECT * FROM (
            SELECT r.schema_name,
                   r.routine_name,
                   r.language_name,
                   r.body,
                   r.is_security_definer,
                   COALESCE((
                       SELECT string_agg(format_type(t, NULL), ',' ORDER BY o)
                       FROM unnest(r.identity_arg_types) WITH ORDINALITY AS a(t, o)), '')
                       AS argument_types,
                   COALESCE((
                       SELECT string_agg(
                           CASE COALESCE(r.arg_modes[i], 'i')
                               WHEN 'i' THEN 'IN'
                               WHEN 'o' THEN 'OUT'
                               WHEN 'b' THEN 'INOUT'
                               WHEN 'v' THEN 'VARIADIC'
                               WHEN 't' THEN 'TABLE'
                           END
                           || chr(31) || COALESCE(r.arg_names[i], '')
                           || chr(31) || format_type(r.all_arg_types[i], NULL),
                           chr(30) ORDER BY i)
                       FROM generate_subscripts(r.all_arg_types, 1) i), '')
                       AS arguments
            FROM routine r
            ) p
            -- The C collation sorts byte-wise, matching the ordinal ordering the
            -- parser-based builder applies. A database-default collation could order
            -- these differently, which would break the hash match.
            ORDER BY p.schema_name COLLATE "C",
                     p.routine_name COLLATE "C",
                     p.argument_types COLLATE "C";
            """;

        var procedures = new List<Element>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, cancellationToken: cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                procedures.Add(PostgresModelFactory.CreateProcedure(
                    reader.GetString("schema_name"),
                    reader.GetString("routine_name"),
                    reader.GetString("argument_types"),
                    reader.GetString("language_name"),
                    reader.GetString("body"),
                    ParseArguments(reader.GetString("arguments")),
                    reader.GetBoolean("is_security_definer")));
            }
        }

        foreach (var procedure in procedures)
        {
            model.Elements.Add(procedure);
        }
    }

    // The extraction query joins each parameter's mode, name and type with a unit
    // separator and the parameters with a record separator. Neither can occur in an
    // identifier or a type name, so splitting is unambiguous — unlike splitting on spaces,
    // which cannot tell an unnamed `double precision` parameter from a named one.
    private const char ParameterPartSeparator = '\u001f';
    private const char ParameterSeparator = '\u001e';

    private static IEnumerable<PostgresModelFactory.ProcedureParameter> ParseArguments(string arguments)
    {
        if (string.IsNullOrEmpty(arguments))
        {
            yield break;
        }

        foreach (var argument in arguments.Split(ParameterSeparator))
        {
            var parts = argument.Split(ParameterPartSeparator);

            if (parts.Length != 3)
            {
                throw new InvalidOperationException(
                    $"Unable to parse procedure parameter '{argument}'");
            }

            // An unnamed parameter comes back with an empty name part.
            yield return new PostgresModelFactory.ProcedureParameter(
                parts[0],
                string.IsNullOrEmpty(parts[1]) ? null : parts[1],
                parts[2]);
        }
    }

    private async Task ExtractAggregatesAsync(Model model, CancellationToken cancellationToken = default)
    {
        // prokind = 'a' selects aggregates. pg_aggregate holds the SFUNC (aggtransfn, an oid
        // into pg_proc) and STYPE (aggtranstype, an oid into pg_type). The SFUNC is reported
        // schema-qualified (namespace.name) to match the parser builder, which qualifies a
        // bare SFUNC with the aggregate's schema. Input types come from the aggregate's own
        // proargtypes; an extension-owned aggregate is skipped as for functions/procedures.
        const string sql =
            """
            SELECT * FROM (
            SELECT n.nspname AS schema_name,
                   p.proname AS routine_name,
                   sn.nspname || '.' || sp.proname AS state_function,
                   format_type(a.aggtranstype, NULL) AS state_type,
                   COALESCE((
                       SELECT string_agg(format_type(t, NULL), ',' ORDER BY o)
                       FROM unnest(p.proargtypes) WITH ORDINALITY AS at(t, o)), '')
                       AS argument_types,
                   COALESCE((
                       SELECT string_agg(
                           'IN' || chr(31) || '' || chr(31) || format_type(t, NULL),
                           chr(30) ORDER BY o)
                       FROM unnest(p.proargtypes) WITH ORDINALITY AS at(t, o)), '')
                       AS arguments
            FROM pg_aggregate a
            JOIN pg_proc p ON p.oid = a.aggfnoid
            JOIN pg_namespace n ON n.oid = p.pronamespace
            JOIN pg_proc sp ON sp.oid = a.aggtransfn
            JOIN pg_namespace sn ON sn.oid = sp.pronamespace
            WHERE p.prokind = 'a'
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND NOT EXISTS (
                  SELECT 1 FROM pg_depend d
                  WHERE d.objid = p.oid AND d.deptype = 'e')
            ) p
            ORDER BY p.schema_name COLLATE "C",
                     p.routine_name COLLATE "C",
                     p.argument_types COLLATE "C";
            """;

        var aggregates = new List<Element>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, cancellationToken: cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                aggregates.Add(PostgresModelFactory.CreateAggregate(
                    reader.GetString("schema_name"),
                    reader.GetString("routine_name"),
                    reader.GetString("argument_types"),
                    reader.GetString("state_function"),
                    reader.GetString("state_type"),
                    ParseArguments(reader.GetString("arguments"))));
            }
        }

        foreach (var aggregate in aggregates)
        {
            model.Elements.Add(aggregate);
        }
    }

    private async Task ExtractTriggersAsync(Model model, CancellationToken cancellationToken = default)
    {
        // pg_trigger.tgtype is a bitmask (see the TRIGGER_TYPE_* macros): bit 0 = ROW,
        // bit 1 = BEFORE, bit 2 = INSERT, bit 3 = DELETE, bit 4 = UPDATE, bit 5 = TRUNCATE,
        // bit 6 = INSTEAD. Internal triggers (tgisinternal, e.g. those implementing FK
        // constraints) are excluded — they are managed by their constraint, not declared. The
        // event list is rendered in the same fixed order the parser builder uses
        // (INSERT, DELETE, UPDATE, TRUNCATE) so the two models hash-match.
        //
        // tgargs is a bytea of null-terminated argument strings; string_to_array on the text
        // splits them and a trailing empty element (from the final terminator) is trimmed. The
        // arguments are re-joined with ', ' to match the parser builder's storage.
        const string sql =
            """
            SELECT n.nspname AS schema_name,
                   c.relname AS table_name,
                   t.tgname AS trigger_name,
                   CASE
                       WHEN (t.tgtype & 64) <> 0 THEN 'INSTEAD OF'
                       WHEN (t.tgtype & 2) <> 0 THEN 'BEFORE'
                       ELSE 'AFTER'
                   END AS timing,
                   array_to_string(ARRAY[]::text[]
                       || CASE WHEN (t.tgtype & 4)  <> 0 THEN ARRAY['INSERT']   ELSE ARRAY[]::text[] END
                       || CASE WHEN (t.tgtype & 8)  <> 0 THEN ARRAY['DELETE']   ELSE ARRAY[]::text[] END
                       || CASE WHEN (t.tgtype & 16) <> 0 THEN ARRAY['UPDATE']   ELSE ARRAY[]::text[] END
                       || CASE WHEN (t.tgtype & 32) <> 0 THEN ARRAY['TRUNCATE'] ELSE ARRAY[]::text[] END,
                       ' OR ') AS events,
                   CASE WHEN (t.tgtype & 1) <> 0 THEN 'ROW' ELSE 'STATEMENT' END AS level,
                   -- A function in public or pg_catalog (built-ins like tsvector_update_trigger
                   -- live in the latter) is reported bare, matching the parser builder, which
                   -- stores a bare function name unqualified so the search path resolves it.
                   CASE WHEN fn.nspname IN ('public', 'pg_catalog') THEN p.proname
                        ELSE fn.nspname || '.' || p.proname END AS trigger_function,
                   COALESCE(
                       array_to_string(
                           (SELECT array_agg(a ORDER BY o)
                            FROM unnest(
                                (SELECT string_to_array(
                                    encode(t.tgargs, 'escape'), E'\\000'))) WITH ORDINALITY AS ta(a, o)
                            WHERE a <> ''),
                           ', '), '') AS function_arguments
            FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_proc p ON p.oid = t.tgfoid
            JOIN pg_namespace fn ON fn.oid = p.pronamespace
            WHERE NOT t.tgisinternal
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
            ORDER BY n.nspname COLLATE "C",
                     c.relname COLLATE "C",
                     t.tgname COLLATE "C";
            """;

        var triggers = new List<Element>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, cancellationToken: cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                triggers.Add(PostgresModelFactory.CreateTrigger(
                    reader.GetString("schema_name"),
                    reader.GetString("trigger_name"),
                    SqlName.Object(reader.GetString("table_name")),
                    reader.GetString("timing"),
                    reader.GetString("events"),
                    reader.GetString("level"),
                    reader.GetString("trigger_function"),
                    reader.GetString("function_arguments")));
            }
        }

        foreach (var trigger in triggers)
        {
            model.Elements.Add(trigger);
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
                rn.nspname AS referenced_schema,
                c.confdeltype AS delete_action,
                c.confupdtype AS update_action,
                k.ordinality AS key_ordinal,
                la.attname AS column_name,
                fa.attname AS referenced_column
            FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_class rt ON rt.oid = c.confrelid
            JOIN pg_namespace rn ON rn.oid = rt.relnamespace
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
                    // Qualify a referenced table in a non-public schema so a cross-schema
                    // FK round-trips with the parser (which keeps the written qualifier).
                    // A public referenced table stays bare, matching an unqualified source
                    // reference — the common case.
                    var referencedSchema = reader.GetString("referenced_schema");
                    var referencedTableName = reader.GetString("referenced_table");
                    var referencedTable = referencedSchema == "public"
                        ? SqlName.Object(referencedTableName)
                        : SqlName.Object(referencedSchema, referencedTableName);

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

    // Extracts the table's UNIQUE constraints (issue #121). Read from pg_constraint rather
    // than information_schema so the key columns come from conkey, which gives their real
    // constraint order — information_schema.constraint_column_usage does not distinguish
    // the ordering of a composite unique key reliably. Unique constraints are modeled as
    // their own SqlUniqueConstraint elements, distinct from the unique SqlIndex that backs
    // them (ExtractIndexesAsync skips constraint-backed indexes, so there is no double-count).
    private async Task ExtractUniqueConstraintsAsync(Model model, TableRef table,
        CancellationToken cancellationToken = default)
    {
        var tableSqlName = SqlName.Object(table.BareName);

        // contype = 'u' is a UNIQUE constraint. The lateral unnest keeps conkey's ordinality
        // so a composite key's columns come back in the order they were declared.
        const string sql = """
            SELECT c.conname AS constraint_name,
                   a.attname AS column_name
            FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS k(attnum, ordinality) ON TRUE
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
            WHERE n.nspname = @schema
              AND t.relname = @name
              AND c.contype = 'u'
            ORDER BY c.conname, k.ordinality;
            """;

        var parameters = new[]
        {
            new DatabaseParameter<string>("@schema", table.Schema),
            new DatabaseParameter<string>("@name", table.BareName),
        };

        // Accumulate rows per constraint (already ordered by column ordinality) so a
        // composite unique constraint becomes a single element.
        var constraints = new Dictionary<string, List<PostgresModelFactory.IndexedColumn>>();
        var order = new List<string>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var constraintName = reader.GetString("constraint_name");

                if (!constraints.TryGetValue(constraintName, out var columns))
                {
                    columns = [];
                    constraints[constraintName] = columns;
                    order.Add(constraintName);
                }

                columns.Add(new PostgresModelFactory.IndexedColumn(
                    tableSqlName.Child(reader.GetString("column_name"))));
            }
        }

        foreach (var constraintName in order)
        {
            model.Elements.Add(PostgresModelFactory.CreateUniqueConstraint(
                SqlName.Object(constraintName), tableSqlName, constraints[constraintName],
                table.Schema));
        }
    }

    // Extracts the table's CHECK constraints (issue #120). pg_get_constraintdef renders the
    // constraint as `CHECK ((<predicate>))`; the model carries just the predicate, since the
    // script generator adds the `CHECK (` wrapper back.
    //
    // NOT NULL is recorded in pg_constraint as a check constraint in PostgreSQL 18+, and the
    // column's IsNullable property already models it, so those are excluded (connoinherit /
    // conkey shape would not distinguish them reliably — pg_constraint.contype = 'c' with a
    // single key column and a `IS NOT NULL` predicate is the marker PostgreSQL itself uses).
    private async Task ExtractCheckConstraintsAsync(Model model, TableRef table,
        CancellationToken cancellationToken = default)
    {
        var tableSqlName = SqlName.Object(table.BareName);

        // contype = 'c' is a CHECK constraint. Ordered by name so extraction is stable.
        // conislocal excludes a constraint inherited from a parent table, which belongs to
        // the parent's declaration rather than this table's.
        const string sql = """
            SELECT c.conname AS constraint_name,
                   pg_get_constraintdef(c.oid) AS constraint_def
            FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname = @schema
              AND t.relname = @name
              AND c.contype = 'c'
              AND c.conislocal
            ORDER BY c.conname COLLATE "C";
            """;

        var parameters = new[]
        {
            new DatabaseParameter<string>("@schema", table.Schema),
            new DatabaseParameter<string>("@name", table.BareName),
        };

        var checks = new List<(string Name, string Definition)>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                checks.Add((reader.GetString("constraint_name"),
                    reader.GetString("constraint_def")));
            }
        }

        foreach (var (name, definition) in checks)
        {
            // A NOT NULL constraint surfaces here on PostgreSQL 18+; the column's
            // IsNullable property already carries it, so modeling it again would produce a
            // constraint the source never declared and a permanent re-diff.
            if (IsNotNullCheckDefinition(definition))
            {
                continue;
            }

            model.Elements.Add(PostgresModelFactory.CreateCheckConstraint(
                SqlName.Object(name), tableSqlName, NormalizeCheckDefinition(definition),
                table.Schema));
        }
    }

    // Whether a check constraint definition is really a NOT NULL constraint, which
    // PostgreSQL 18+ records in pg_constraint as `CHECK ((<column> IS NOT NULL))`.
    private static bool IsNotNullCheckDefinition(string constraintDef)
        => constraintDef.EndsWith("IS NOT NULL))", StringComparison.Ordinal)
           || constraintDef.EndsWith("IS NOT NULL)", StringComparison.Ordinal);

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
                c.numeric_precision,
                c.numeric_scale,
                c.is_identity,
                c.identity_generation,
                c.identity_start,
                c.identity_increment,
                c.identity_minimum,
                c.identity_maximum,
                c.identity_cycle,
                seq.seqcache AS identity_cache,
                c.column_default,
                c.udt_name,
                -- A domain-typed column reports its base type in data_type but its domain in
                -- domain_name; the model carries the domain name, so it is read here (#84).
                c.domain_name,
                -- The typtype of the column's own type: 'd' marks a domain, whose column
                -- information_schema reports as its base type (issue #84). Resolved from
                -- pg_attribute.atttypid so a domain-typed column can adopt the domain name.
                col_type.typtype::text AS col_typtype,
                format_type(a.atttypid, a.atttypmod) AS formatted_type,
                -- A generated (computed) column (issue #120). attgenerated is 's' for a
                -- STORED generated column and '' otherwise; the generation expression lives
                -- in pg_attrdef alongside ordinary defaults, so it is read through
                -- pg_get_expr rather than information_schema.column_default (which reports
                -- NULL for a generated column).
                a.attgenerated::text AS generated_kind,
                pg_get_expr(ad.adbin, ad.adrelid) AS generation_expression
            FROM information_schema.columns c
            JOIN pg_namespace n ON n.nspname = c.table_schema
            JOIN pg_class t ON t.relname = c.table_name AND t.relnamespace = n.oid
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attname = c.column_name
            JOIN pg_type col_type ON col_type.oid = a.atttypid
            -- An identity column's CACHE lives on its implicit sequence (pg_sequence),
            -- which information_schema does not expose; the sequence is found through
            -- its internal ('i') dependency on the column.
            LEFT JOIN pg_depend dep ON dep.refclassid = 'pg_class'::regclass
                AND dep.refobjid = t.oid
                AND dep.refobjsubid = a.attnum
                AND dep.classid = 'pg_class'::regclass
                AND dep.deptype = 'i'
            LEFT JOIN pg_sequence seq ON seq.seqrelid = dep.objid
            LEFT JOIN pg_attrdef ad ON ad.adrelid = t.oid AND ad.adnum = a.attnum
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
            var numericPrecision = reader.GetFieldValue<int?>("numeric_precision");
            var numericScale = reader.GetFieldValue<int?>("numeric_scale");
            var isIdentity = reader.GetString("is_identity") == "YES";
            var columnTypeType = reader.GetString("col_typtype");

            // A domain-typed column (pg_type.typtype = 'd') reports its base type in
            // information_schema.data_type (e.g. a DOMAIN over integer reports "integer"),
            // but the parser builder carries the column type as the domain name — so the
            // two models would disagree and the hash comparison would fail. domain_name
            // holds the domain's own name (udt_name is the base type's internal name here,
            // e.g. "int4"), so adopt it, and drop any base-type length / precision that
            // information_schema reported: the parser side emits the domain name with no
            // modifiers, so both sides must (issue #84).
            if (columnTypeType == "d")
            {
                dataType = reader.GetString("domain_name");
                maxLength = null;
                numericPrecision = null;
                numericScale = null;
            }
            // For a user-defined type the canonical name is udt_name (e.g. "vector"), not
            // the generic "USER-DEFINED" that data_type reports. The type modifier (a
            // vector's dimension) is recovered from the format_type() text and mapped to
            // the same Length property the parser builder uses, so both sides hash-match.
            else if (dataType == "USER-DEFINED")
            {
                dataType = reader.GetString("udt_name");

                var formattedType = reader.GetString("formatted_type");

                if (TryParseTypeModifier(formattedType, out var modifier))
                {
                    maxLength = modifier;
                }
            }
            // An array column reports data_type = 'ARRAY'; format_type() renders the
            // canonical array notation (e.g. "text[]"), which is exactly the name the
            // parser builder emits (element canonical name + "[]"). information_schema
            // reports NULL character_maximum_length / numeric_precision for arrays, so no
            // Length/Precision/Scale property is emitted on either side (issue #76).
            else if (dataType == "ARRAY")
            {
                dataType = reader.GetString("formatted_type");
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

            // For a `numeric(p, s)` column, mirror the parser builder by emitting
            // Precision and Scale (issue #33). information_schema reports a
            // numeric_precision for other types too (e.g. integer -> 32), so this is
            // gated on the numeric type. A bare, unconstrained `numeric` reports a
            // NULL numeric_precision — the parser emits no modifiers there, so we
            // emit none either. Stored as long to match the parser's value type.
            if (dataType == "numeric" && numericPrecision.HasValue)
            {
                typeElement.Properties.Add(
                    new Property(PostgresPropertyNames.Precision, (long)numericPrecision.Value));
                typeElement.Properties.Add(
                    new Property(PostgresPropertyNames.Scale, (long)(numericScale ?? 0)));
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

                // Sequence options (issue #13): information_schema reports every option
                // with defaults filled in (identity_start = 1 for a plain identity), so
                // values equal to the type/direction default are omitted — mirroring the
                // parser builder, which stores only what was written and non-default.
                // The identity_* columns are character_data; parse them as longs.
                var startValue = long.Parse(reader.GetString("identity_start"),
                    System.Globalization.CultureInfo.InvariantCulture);
                var increment = long.Parse(reader.GetString("identity_increment"),
                    System.Globalization.CultureInfo.InvariantCulture);
                var minValue = long.Parse(reader.GetString("identity_minimum"),
                    System.Globalization.CultureInfo.InvariantCulture);
                var maxValue = long.Parse(reader.GetString("identity_maximum"),
                    System.Globalization.CultureInfo.InvariantCulture);
                var isCycling = reader.GetString("identity_cycle") == "YES";
                var cacheSize = reader.GetFieldValue<long?>("identity_cache")
                    ?? PostgresIdentitySequenceDefaults.CacheSize;

                var (defaultStart, defaultMin, defaultMax) =
                    PostgresIdentitySequenceDefaults.For(dataType, increment);

                if (startValue != defaultStart)
                {
                    column.Properties.Add(new Property(PostgresPropertyNames.StartValue, startValue));
                }

                if (increment != PostgresIdentitySequenceDefaults.Increment)
                {
                    column.Properties.Add(new Property(PostgresPropertyNames.Increment, increment));
                }

                if (minValue != defaultMin)
                {
                    column.Properties.Add(new Property(PostgresPropertyNames.MinValue, minValue));
                }

                if (maxValue != defaultMax)
                {
                    column.Properties.Add(new Property(PostgresPropertyNames.MaxValue, maxValue));
                }

                if (cacheSize != PostgresIdentitySequenceDefaults.CacheSize)
                {
                    column.Properties.Add(new Property(PostgresPropertyNames.CacheSize, cacheSize));
                }

                if (isCycling != PostgresIdentitySequenceDefaults.IsCycling)
                {
                    column.Properties.Add(new Property(PostgresPropertyNames.IsCycling, isCycling));
                }
            }

            // A serial column's default is a nextval(...) sequence call, and an identity
            // column has no default; PostgresDefaultValue models neither, so only a genuine
            // constant-literal default is recorded. Emitted after identity so the property
            // order matches the parser builder (the Merkle hash is order-sensitive).
            var columnDefault = reader.IsDBNull(reader.GetOrdinal("column_default"))
                ? null
                : reader.GetString("column_default");

            if (PostgresDefaultValue.FromDatabaseText(columnDefault) is { } defaultValue)
            {
                column.Properties.Add(new Property(PostgresPropertyNames.DefaultValue, defaultValue));
            }

            // A generated column (issue #120). PostgreSQL supports only STORED generation,
            // so attgenerated is 's' or empty. The expression comes back rewritten by
            // pg_get_expr, which is why it does not take part in comparison — see
            // PostgresModelFactory.AddGeneratedColumnProperties.
            if (reader.GetString("generated_kind") == "s")
            {
                var generationExpression = reader.IsDBNull(reader.GetOrdinal("generation_expression"))
                    ? null
                    : reader.GetString("generation_expression");

                PostgresModelFactory.AddGeneratedColumnProperties(column, generationExpression);
            }

            columns.Entries.Add(column);
        }
    }
}
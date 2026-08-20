using System.Data.Common;
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

        // Collations (issue #159) depend on nothing but their schema, and a column's COLLATE may
        // name one, so they precede the tables for the same reason the types below do.
        await ExtractCollationsAsync(model, cancellationToken);

        // Enum types and domains (issue #75) are user-defined types a column may be typed
        // as, so they must precede the tables in the model — both for a hash-matching
        // element order and so CREATE TYPE / CREATE DOMAIN run before the CREATE TABLE.
        await ExtractEnumTypesAsync(model, cancellationToken);
        await ExtractDomainsAsync(model, cancellationToken);

        // Composite and range types (issue #122) are likewise types a column may be declared
        // as, so they precede the tables for the same reasons as enums and domains.
        await ExtractCompositeTypesAsync(model, cancellationToken);
        await ExtractRangeTypesAsync(model, cancellationToken);

        // Standalone sequences (issue #122) likewise precede tables: a column default may draw
        // from one via nextval(), so the CREATE SEQUENCE must run before the CREATE TABLE.
        await ExtractSequencesAsync(model, cancellationToken);

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

            // EXCLUDE constraints follow the CHECK constraints and precede the foreign keys,
            // again matching the parser-based builder's order (issue #212).
            await ExtractExclusionConstraintsAsync(model, table, cancellationToken);

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
            var typeSpecifier = MakeDomainTypeSpecifierElement(baseType);

            model.Elements.Add(
                PostgresModelFactory.CreateDomain(SqlName.Object(name), schema, typeSpecifier, check));
        }
    }

    private async Task ExtractCompositeTypesAsync(Model model,
        CancellationToken cancellationToken = default)
    {
        // Every declared composite type with its attributes in declaration order (attnum).
        //
        // The load-bearing clause is `c.relkind = 'c'`. PostgreSQL gives every table, view and
        // index a composite row type in pg_type with typtype = 'c', so filtering on typtype
        // alone would model a phantom composite type for every table in the database. Only a
        // relkind of 'c' is a standalone, declared composite type.
        //
        // Dropped attributes stay in pg_attribute with attisdropped set, so they are excluded;
        // system columns (attnum <= 0) likewise. Extension-owned types are skipped as for
        // enums and domains, and the ordering matches the parser builder's.
        const string sql =
            """
            SELECT n.nspname AS schema_name,
                   t.typname AS type_name,
                   a.attname AS attribute_name,
                   format_type(a.atttypid, a.atttypmod) AS attribute_type
            FROM pg_type t
            JOIN pg_namespace n ON n.oid = t.typnamespace
            JOIN pg_class c ON c.oid = t.typrelid
            JOIN pg_attribute a ON a.attrelid = c.oid
            WHERE t.typtype = 'c'
              AND c.relkind = 'c'
              AND a.attnum > 0
              AND NOT a.attisdropped
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND NOT EXISTS (SELECT 1 FROM pg_depend d
                              WHERE d.objid = t.oid AND d.deptype = 'e')
            ORDER BY n.nspname COLLATE "C", t.typname COLLATE "C", a.attnum;
            """;

        // Accumulate attributes per (schema, type) preserving encounter order (the query is
        // ordered by attnum), then build one element per composite type.
        var types = new List<(string Schema, string Name, List<(string Name, string Type)> Attributes)>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, cancellationToken: cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString("schema_name");
                var name = reader.GetString("type_name");

                // The list holds a reference to each attribute list, so appending through it
                // mutates the entry already in `types` (the tuple itself is never rewritten).
                if (types.Count == 0 || types[^1].Schema != schema || types[^1].Name != name)
                {
                    types.Add((schema, name, new List<(string, string)>()));
                }

                types[^1].Attributes.Add(
                    (reader.GetString("attribute_name"), reader.GetString("attribute_type")));
            }
        }

        foreach (var (schema, name, attributes) in types)
        {
            var typeName = SqlName.Object(name);

            var attributeElements = attributes.Select(attribute =>
                new Element(PostgresElementTypes.SqlSimpleColumn)
                {
                    Name = typeName.Child(attribute.Name),
                    Relationships =
                    {
                        new Relationship(PostgresRelationshipNames.TypeSpecifier)
                        {
                            // format_type() renders any modifier inline (character varying(60)),
                            // while the parser builder carries the bare type plus a Length /
                            // Precision / Scale property — the same split a domain's base type
                            // needs, so the same helper applies.
                            MakeDomainTypeSpecifierElement(attribute.Type),
                        },
                    },
                });

            model.Elements.Add(
                PostgresModelFactory.CreateCompositeType(typeName, schema, attributeElements));
        }
    }

    private async Task ExtractRangeTypesAsync(Model model, CancellationToken cancellationToken = default)
    {
        // Every declared range type with its subtype, operator class and collation.
        //
        // typtype = 'r' selects range types only. PostgreSQL creates a companion *multirange*
        // type for each one, but that is typtype = 'm', so it is excluded by construction and
        // never modeled as its own declared object.
        //
        // The catalog always reports a resolved operator class, so opcdefault distinguishes the
        // one PostgreSQL picked from one the source named; only a non-default is stored, which
        // is what lets a declared range hash-match an extracted one.
        //
        // Collation gets the same treatment for the same reason: a collatable subtype (text)
        // resolves to the collation named "default" even when the source named none, so that
        // one is normalized away — otherwise every text-based range would look changed on
        // every deploy. rngcollation is 0 outright when the subtype is not collatable.
        const string sql =
            """
            SELECT n.nspname AS schema_name,
                   t.typname AS type_name,
                   format_type(r.rngsubtype, NULL) AS subtype,
                   CASE WHEN opc.opcdefault THEN NULL ELSE opc.opcname END AS opclass,
                   CASE WHEN coll.collname = 'default' THEN NULL ELSE coll.collname END
                       AS collation_name
            FROM pg_range r
            JOIN pg_type t ON t.oid = r.rngtypid
            JOIN pg_namespace n ON n.oid = t.typnamespace
            JOIN pg_opclass opc ON opc.oid = r.rngsubopc
            LEFT JOIN pg_collation coll ON coll.oid = r.rngcollation
            WHERE t.typtype = 'r'
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND NOT EXISTS (SELECT 1 FROM pg_depend d
                              WHERE d.objid = t.oid AND d.deptype = 'e')
            ORDER BY n.nspname COLLATE "C", t.typname COLLATE "C";
            """;

        var ranges = new List<Element>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, cancellationToken: cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var opclassOrdinal = reader.GetOrdinal("opclass");
                var collationOrdinal = reader.GetOrdinal("collation_name");

                ranges.Add(PostgresModelFactory.CreateRangeType(
                    SqlName.Object(reader.GetString("type_name")),
                    reader.GetString("schema_name"),
                    reader.GetString("subtype"),
                    reader.IsDBNull(opclassOrdinal) ? null : reader.GetString("opclass"),
                    reader.IsDBNull(collationOrdinal) ? null : reader.GetString("collation_name")));
            }
        }

        foreach (var range in ranges)
        {
            model.Elements.Add(range);
        }
    }

    private async Task ExtractCollationsAsync(Model model, CancellationToken cancellationToken = default)
    {
        // Every user-declared collation (issue #159).
        //
        // pg_collation is largely populated by the initdb-time import of the host's locales —
        // hundreds of rows that are not declared objects and differ between machines. Those come
        // from the pg_catalog schema, so restricting to user schemas leaves only the declared
        // ones. An extension-owned collation is excluded the same way range types exclude one.
        //
        // collprovider is a single char ('c' libc, 'i' icu, 'b' builtin). collcollate/collctype
        // are populated for libc and empty for icu, which uses the locale column instead — the
        // empty ones come back NULL so the model stores only the facets that actually apply.
        //
        // That locale column is spelled differently across the majors Squill supports (measured:
        // absent on 14, colliculocale on 15-16, colllocale on 17+), and naming a missing column
        // is a parse-time error even inside a CASE that never runs. Reading it through
        // to_jsonb() resolves the name at run time instead, so one query works on every major.
        const string sql =
            """
            SELECT n.nspname AS schema_name,
                   c.collname AS collation_name,
                   c.collprovider::text AS provider,
                   nullif(coalesce(to_jsonb(c) ->> 'colllocale',
                                   to_jsonb(c) ->> 'colliculocale'), '') AS locale,
                   nullif(c.collcollate, '') AS lc_collate,
                   nullif(c.collctype, '') AS lc_ctype,
                   c.collisdeterministic AS is_deterministic
            FROM pg_collation c
            JOIN pg_namespace n ON n.oid = c.collnamespace
            WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND NOT EXISTS (SELECT 1 FROM pg_depend d
                              WHERE d.objid = c.oid AND d.deptype = 'e')
            ORDER BY n.nspname COLLATE "C", c.collname COLLATE "C";
            """;

        var collations = new List<Element>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, cancellationToken: cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var localeOrdinal = reader.GetOrdinal("locale");
                var lcCollateOrdinal = reader.GetOrdinal("lc_collate");
                var lcCtypeOrdinal = reader.GetOrdinal("lc_ctype");

                collations.Add(PostgresModelFactory.CreateCollation(
                    SqlName.Object(reader.GetString("collation_name")),
                    reader.GetString("schema_name"),
                    MapCollationProvider(reader.GetString("provider")),
                    reader.IsDBNull(localeOrdinal) ? null : reader.GetString("locale"),
                    reader.IsDBNull(lcCollateOrdinal) ? null : reader.GetString("lc_collate"),
                    reader.IsDBNull(lcCtypeOrdinal) ? null : reader.GetString("lc_ctype"),
                    reader.GetBoolean("is_deterministic")));
            }
        }

        foreach (var collation in collations)
        {
            model.Elements.Add(collation);
        }
    }

    // pg_collation stores the provider as a single char; the model carries the name the source
    // writes in PROVIDER = ....
    private static string MapCollationProvider(string code)
        => code switch
        {
            "c" => "libc",
            "i" => "icu",
            "b" => "builtin",
            _ => throw new InvalidOperationException($"Unknown collation provider: {code}"),
        };

    private async Task ExtractSequencesAsync(Model model, CancellationToken cancellationToken = default)
    {
        // Every standalone sequence, with its options from pg_sequence (which always reports
        // each one with the defaults filled in — the model factory drops those again).
        //
        // The load-bearing clause is the pg_depend exclusion. PostgreSQL creates a sequence
        // behind every serial and identity column, and those are modeled as part of the column,
        // not as elements: extracting them would put a sequence in the target model that no
        // source declares, so every schema with a serial column would show a phantom drop (or,
        // with DropObjectsNotInSource, actually lose the sequence its column depends on).
        //
        // Both kinds are found the same way: an identity column's sequence is an internal ('i')
        // dependency, a serial column's an auto ('a') one. An explicitly declared OWNED BY
        // sequence is indistinguishable from the serial case here — which is exactly why the
        // parser rejects OWNED BY, so no declared sequence can land in this excluded set.
        //
        // Extension-owned sequences ('e') are excluded as for enums and domains, and ordered by
        // schema then name (COLLATE "C") to match the parser builder's ordering.
        const string sql =
            """
            SELECT n.nspname AS schema_name,
                   c.relname AS sequence_name,
                   format_type(s.seqtypid, NULL) AS data_type,
                   s.seqstart AS start_value,
                   s.seqincrement AS increment_by,
                   s.seqmin AS min_value,
                   s.seqmax AS max_value,
                   s.seqcache AS cache_size,
                   s.seqcycle AS is_cycling
            FROM pg_sequence s
            JOIN pg_class c ON c.oid = s.seqrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND NOT EXISTS (SELECT 1 FROM pg_depend d
                              WHERE d.objid = c.oid
                                AND d.classid = 'pg_class'::regclass
                                AND d.deptype IN ('a', 'i', 'e'))
            ORDER BY n.nspname COLLATE "C", c.relname COLLATE "C";
            """;

        var sequences = new List<Element>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, cancellationToken: cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                sequences.Add(PostgresModelFactory.CreateSequence(
                    SqlName.Object(reader.GetString("sequence_name")),
                    reader.GetString("schema_name"),
                    reader.GetString("data_type"),
                    reader.GetInt64(reader.GetOrdinal("start_value")),
                    reader.GetInt64(reader.GetOrdinal("increment_by")),
                    reader.GetInt64(reader.GetOrdinal("min_value")),
                    reader.GetInt64(reader.GetOrdinal("max_value")),
                    reader.GetInt64(reader.GetOrdinal("cache_size")),
                    reader.GetBoolean(reader.GetOrdinal("is_cycling"))));
            }
        }

        foreach (var sequence in sequences)
        {
            model.Elements.Add(sequence);
        }
    }

    // Builds a domain's base-type specifier from what format_type() renders, which includes any
    // modifier inline — `character varying(5)`, `numeric(10,2)`. The parser builder instead
    // carries the bare canonical type name plus Length (or Precision/Scale) properties, the same
    // shape a column uses, so the modifier is split out here for the two to hash-match. Without
    // this a domain declared with any modifier looked different on every deploy, and — since a
    // domain's base type cannot be altered — that difference then failed the deploy (issue #122).
    private static Element MakeDomainTypeSpecifierElement(string formattedType)
    {
        var open = formattedType.IndexOf('(');

        if (open < 0 || !formattedType.EndsWith(')'))
        {
            return MakeTypeSpecifierElement(formattedType);
        }

        var typeName = formattedType[..open].Trim();
        var modifiers = formattedType[(open + 1)..^1].Split(',');

        var element = MakeTypeSpecifierElement(typeName);

        // numeric(p, s) carries precision and scale; every other modified type Squill models
        // (character varying, character, bit, bit varying) carries a single length. Values are
        // stored with the same CLR types the parser builder uses, or the hashes differ.
        // A numeric carries precision and scale. The catalog cannot distinguish `numeric(10)`
        // from `numeric(10, 0)` — format_type renders both as `numeric(10,0)` — so this matches
        // the explicit two-modifier spelling, which is what the parser builder models. A domain
        // declared `numeric(10)` is the one form that still differs; it is unambiguous in the
        // source but genuinely unrecoverable from the database.
        if (typeName == "numeric")
        {
            if (modifiers.Length > 1
                && long.TryParse(modifiers[0].Trim(), out var precision)
                && long.TryParse(modifiers[1].Trim(), out var scale))
            {
                element.Properties.Add(new Property(PostgresPropertyNames.Precision, precision));
                element.Properties.Add(new Property(PostgresPropertyNames.Scale, scale));
            }
        }
        else if (modifiers.Length == 1 && int.TryParse(modifiers[0].Trim(), out var length))
        {
            element.Properties.Add(new Property(PostgresPropertyNames.Length, length));
        }

        return element;
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

        // A NO INHERIT check renders as `CHECK ((b > 0)) NO INHERIT` -- the clause is a suffix
        // outside the predicate's parentheses, and is carried as its own property (issue #205).
        // Left in place it would both defeat the paren-stripping below and bake the clause into
        // the predicate, so the constraint would re-diff on every deploy.
        const string noInheritSuffix = " NO INHERIT";
        if (text.EndsWith(noInheritSuffix, StringComparison.Ordinal))
        {
            text = text[..^noInheritSuffix.Length].Trim();
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
                         AND NOT a.attisdropped), '') AS column_names,
                   -- Issue #208. Read from reloptions, which is where PostgreSQL puts both
                   -- the WITH (...) options and the trailing WITH CHECK OPTION clause: the
                   -- clause form is stored as check_option=cascaded/local, indistinguishable
                   -- from the reloption spelling, so one read covers both.
                   (SELECT o FROM unnest(c.reloptions) o
                    WHERE o LIKE 'check_option=%') AS check_option,
                   (SELECT o FROM unnest(c.reloptions) o
                    WHERE o LIKE 'security_invoker=%') AS security_invoker,
                   (SELECT o FROM unnest(c.reloptions) o
                    WHERE o LIKE 'security_barrier=%') AS security_barrier
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
                    definition: null,
                    ReloptionValue(reader, "check_option")?.ToUpperInvariant(),
                    ReloptionFlag(reader, "security_invoker"),
                    ReloptionFlag(reader, "security_barrier")));
            }
        }

        foreach (var view in views)
        {
            model.Elements.Add(view);
        }
    }

    // Column names are joined with a record separator, which cannot occur in an identifier.
    private const char ViewColumnSeparator = '';


    // A reloptions entry arrives as the whole "name=value" string, so the value is what
    // follows the first '='. Null when the view declared no such option.
    private static string? ReloptionValue(DbDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);

        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var entry = reader.GetString(ordinal);
        var separator = entry.IndexOf('=');

        return separator < 0 ? null : entry[(separator + 1)..];
    }

    // Null when absent, which is a different state from an explicitly written false:
    // PostgreSQL records security_invoker=false in reloptions rather than dropping it
    // (measured), so both must survive the round trip distinctly or a view declaring the
    // default would re-diff on every deploy.
    private static bool? ReloptionFlag(DbDataReader reader, string column)
        => ReloptionValue(reader, column) is { } value
            ? value.Equals("true", StringComparison.OrdinalIgnoreCase)
            : null;
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
                       -- proconfig is the SET clauses as `name=value` entries in declaration
                       -- order (issue #213); flattened with the same separator the parsed
                       -- model uses, which cannot occur in a GUC name or value.
                       array_to_string(p.proconfig, chr(30)) AS configuration,
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
                  -- 'e' excludes extension-owned functions. 'i' excludes functions
                  -- PostgreSQL creates internally for another object — notably the
                  -- constructor functions of a range type, which are not declared and
                  -- cannot be dropped independently of the type (issue #122).
                  AND NOT EXISTS (
                      SELECT 1 FROM pg_depend d
                      WHERE d.objid = p.oid AND d.deptype IN ('e', 'i'))
            )
            SELECT * FROM (
            SELECT r.schema_name,
                   r.routine_name,
                   r.language_name,
                   r.body,
                   r.is_security_definer,
                   r.configuration,
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
                    reader.GetBoolean("is_security_definer"),
                    // A routine with no SET clause reports a null proconfig, which the model
                    // stores as an absent property rather than an empty one.
                    reader.IsDBNull(reader.GetOrdinal("configuration"))
                        ? null
                        : reader.GetString("configuration")));
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
                       -- proconfig is the SET clauses as `name=value` entries in declaration
                       -- order (issue #213), flattened with the separator the parsed model uses.
                       array_to_string(p.proconfig, chr(30)) AS configuration,
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
                   r.configuration,
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
                    reader.GetBoolean("is_security_definer"),
                    reader.IsDBNull(reader.GetOrdinal("configuration"))
                        ? null
                        : reader.GetString("configuration")));
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
                           ', '), '') AS function_arguments,
                   -- The WHEN predicate. pg_get_triggerdef is the only route to it: tgqual is
                   -- a serialized node tree, not text, so it is sliced out of the rendered
                   -- definition on the ' WHEN (' and ') EXECUTE ' delimiters the engine emits.
                   --
                   -- Both .* are GREEDY, which anchors the match on the LAST occurrence of each
                   -- delimiter. That is load-bearing: a predicate may contain either delimiter
                   -- inside a string literal, and measured, `WHEN (new.s <> ') EXECUTE ')` is
                   -- stored verbatim, so a leftmost match would truncate it. The trailing
                   -- EXECUTE names a function and its arguments and never introduces another
                   -- WHEN, so the last delimiter is always the real one.
                   CASE WHEN t.tgqual IS NOT NULL THEN
                       regexp_replace(
                           pg_get_triggerdef(t.oid),
                           '^.* WHEN \((.*)\) EXECUTE .*$', '\1')
                   END AS when_condition,
                   -- tgattr holds the UPDATE OF column attnums in DECLARED order, which is the
                   -- order PostgreSQL renders them back in, so the ordinality is preserved
                   -- rather than sorted by attnum.
                   (SELECT string_agg(a.attname, ', ' ORDER BY k.ord)
                    FROM unnest(t.tgattr::int2[]) WITH ORDINALITY AS k(attnum, ord)
                    JOIN pg_attribute a
                      ON a.attrelid = t.tgrelid AND a.attnum = k.attnum) AS update_of_columns,
                   t.tgoldtable AS old_transition_table,
                   t.tgnewtable AS new_transition_table,
                   t.tgconstraint <> 0 AS is_constraint_trigger,
                   t.tgdeferrable AS is_deferrable,
                   t.tginitdeferred AS is_initially_deferred
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
                    reader.GetString("function_arguments"),
                    new PostgresModelFactory.TriggerModifiers(
                        WhenCondition: reader.IsDBNull("when_condition") ? null : reader.GetString("when_condition"),
                        UpdateOfColumns: reader.IsDBNull("update_of_columns") ? null : reader.GetString("update_of_columns"),
                        OldTransitionTable: reader.IsDBNull("old_transition_table") ? null : reader.GetString("old_transition_table"),
                        NewTransitionTable: reader.IsDBNull("new_transition_table") ? null : reader.GetString("new_transition_table"),
                        IsConstraintTrigger: reader.GetBoolean("is_constraint_trigger"),
                        IsDeferrable: reader.GetBoolean("is_deferrable"),
                        IsInitiallyDeferred: reader.GetBoolean("is_initially_deferred"))));
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
                -- Deferrability (issue #159). Both are false for an ordinary constraint, which
                -- is the Postgres default, so the model stores each only when true.
                c.condeferrable AS is_deferrable,
                c.condeferred AS is_initially_deferred,
                -- MATCH type (issue #205), a single char: 'f' = FULL, 's' = SIMPLE,
                -- 'p' = PARTIAL. Both an omitted clause and an explicit MATCH SIMPLE report
                -- 's', so only 'f' is modeled.
                c.confmatchtype AS match_type,
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
                        MapReferentialAction(reader.GetFieldValue<char>("update_action")),
                        reader.GetBoolean("is_deferrable"),
                        reader.GetBoolean("is_initially_deferred"),
                        reader.GetFieldValue<char>("match_type") == 'f');

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
                accumulator.OnUpdate,
                accumulator.IsDeferrable,
                accumulator.IsInitiallyDeferred,
                table.Schema,
                accumulator.IsMatchFull));
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
        // indkey spans key columns *and* INCLUDE columns: positions 1..indnkeyatts are the key,
        // indnkeyatts+1..indnatts the covering columns. is_included partitions the rows so a
        // covering column is not mistaken for a key one. The opclass join is LEFT because
        // indclass has entries only for key columns (issue #160).
        //
        // A parameterized operator class (PostgreSQL 13+, issue #211) keeps its parameters in
        // pg_attribute.attoptions on the *index* relation, keyed by the key column's ordinal
        // (measured), not in indclass, which holds only the opclass oid. That is why the
        // opcdefault suppression above is conditional on there being none: measured,
        // `gist (tsv tsvector_ops(siglen=256))` resolves to the type's *default* opclass, yet
        // PostgreSQL rejects the parameters without an explicit class name ("column siglen does
        // not exist"), so suppressing the name there would emit DDL the server refuses.
        //
        // indcollation is an oidvector, which unlike a normal Postgres array is 0-based —
        // measured, indcollation[0] is the first key column — and it too spans key columns
        // only. A collation is surfaced only when it differs from the column type's own
        // (typcollation), mirroring the opcdefault suppression beside it: every collatable
        // column reports a resolved collation ("default", oid 100), so recording it
        // unconditionally would make every text index re-diff on every deploy.
        //
        // indnullsnotdistinct does not exist before PostgreSQL 15, and naming a missing column
        // is a parse-time error even where it is never read, so it is read through to_jsonb()
        // to resolve the name at run time — the same trick #159 used for the collation locale.
        //
        // An expression key (lower(name)) has indkey entry 0, which matches no pg_attribute
        // row, so that join is LEFT or the whole index would vanish from the extract. Its text
        // comes from the per-column form of pg_get_indexdef, which renders the one canonical
        // spelling PostgreSQL stores for both `(lower(name))` and `((lower(name)))`.
        const string sql = """
            SELECT
                i.relname AS index_name,
                ix.indisunique AS is_unique,
                am.amname AS index_method,
                pg_get_expr(ix.indpred, ix.indrelid) AS filter_predicate,
                a.attname AS column_name,
                k.ordinality AS column_ordinal,
                k.ordinality > ix.indnkeyatts AS is_included,
                pg_get_indexdef(ix.indexrelid, k.ordinality::integer, true) AS key_expression,
                ix.indoption[k.ordinality - 1] AS column_option,
                CASE WHEN oc.opcdefault AND ia.attoptions IS NULL THEN NULL
                     ELSE oc.opcname END AS operator_class,
                array_to_string(ia.attoptions, ', ') AS operator_class_parameters,
                CASE WHEN ix.indcollation[k.ordinality - 1] <> ty.typcollation
                     THEN co.collname END AS collation_name,
                coalesce((to_jsonb(ix) ->> 'indnullsnotdistinct')::boolean, false)
                    AS nulls_not_distinct,
                array_to_string(i.reloptions, ', ') AS storage_parameters
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_class t ON t.oid = ix.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_am am ON am.oid = i.relam
            JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ordinality) ON TRUE
            LEFT JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
            LEFT JOIN pg_type ty ON ty.oid = a.atttypid
            LEFT JOIN pg_opclass oc ON oc.oid = ix.indclass[k.ordinality - 1]
            LEFT JOIN pg_attribute ia
              ON ia.attrelid = ix.indexrelid AND ia.attnum = k.ordinality
            LEFT JOIN pg_collation co ON co.oid = ix.indcollation[k.ordinality - 1]
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
        var indexRows = new Dictionary<string, (bool IsUnique, string Method, string? FilterPredicate, string? StorageParameters, bool NullsNotDistinct, List<PostgresModelFactory.IndexedColumn> Columns, List<SqlName> IncludedColumns)>();
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

                    entry = (reader.GetBoolean("is_unique"), reader.GetString("index_method"),
                        filterPredicate, storageParameters,
                        reader.GetBoolean("nulls_not_distinct"), new(), new());
                    indexRows.Add(indexName, entry);
                    order.Add(indexName);
                }

                // NULL for an expression key, whose indkey entry is 0 and so matches no column.
                var columnName = reader.IsDBNull("column_name")
                    ? null
                    : reader.GetString("column_name");

                // An INCLUDE column is stored in the index but is not part of its key: it
                // carries no ordering, opclass or collation, so it is recorded by name alone.
                if (reader.GetBoolean("is_included"))
                {
                    if (columnName is not null)
                    {
                        entry.IncludedColumns.Add(tableSqlName.Child(columnName));
                    }

                    continue;
                }

                var keyExpression = columnName is null
                    ? reader.GetString("key_expression")
                    : null;

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

                var collation = reader.IsDBNull("collation_name")
                    ? null
                    : reader.GetString("collation_name");

                var operatorClassParameters = reader.IsDBNull("operator_class_parameters")
                    ? null
                    : reader.GetString("operator_class_parameters");

                entry.Columns.Add(new PostgresModelFactory.IndexedColumn(
                    // An expression key names no column, so the index's own name stands in to
                    // give the spec a stable identity — matching the parser builder.
                    columnName is not null
                        ? tableSqlName.Child(columnName)
                        : SqlName.Object(indexName),
                    IsAscending: isAscending,
                    NullsFirst: nullsFirst,
                    OperatorClass: operatorClass,
                    Collation: collation,
                    KeyExpression: keyExpression,
                    OperatorClassParameters: operatorClassParameters));
            }
        }

        foreach (var indexName in order)
        {
            var (isUnique, method, filterPredicate, storageParameters, nullsNotDistinct, columns,
                includedColumns) = indexRows[indexName];

            model.Elements.Add(PostgresModelFactory.CreateIndex(
                SqlName.Object(indexName), tableSqlName, isUnique, method, columns,
                filterPredicate, storageParameters, schema, includedColumns, nullsNotDistinct));
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

        // Keyed on constraint_schema, the same schema ExtractPrimaryKeyColumnsAsync uses, so
        // both lookups of this one constraint agree on where it lives.
        var (includedColumns, storageParameters) =
            await ExtractConstraintIndexFacetsAsync(constraintSchema, name, tableSqlName, cancellationToken);

        model.Elements.Add(PostgresModelFactory.CreatePrimaryKey(
            SqlName.Object(name), tableSqlName, columns, table.Schema,
            includedColumns, storageParameters));
    }

    /// <summary>
    /// The INCLUDE columns and WITH (...) storage parameters of a constraint, read from the
    /// index backing it (issue #210).
    ///
    /// Both live on that index rather than on the constraint row: the covering columns are the
    /// attributes past <c>pg_index.indnkeyatts</c>, and the storage parameters are the index's
    /// <c>pg_class.reloptions</c>, which <c>pg_get_constraintdef</c> does not render at all.
    /// </summary>
    private async Task<(IReadOnlyList<SqlName> Included, string? StorageParameters)>
        ExtractConstraintIndexFacetsAsync(
            string schema, string constraintName, SqlName tableSqlName,
            CancellationToken cancellationToken)
    {
        // attnum > indnkeyatts is exactly the INCLUDE set: PostgreSQL stores the covering
        // columns after the key columns, and indnkeyatts is the count of the key ones.
        const string sql = """
            SELECT a.attname AS column_name,
                   array_to_string(i.reloptions, ', ') AS storage_parameters
            FROM pg_constraint c
            JOIN pg_class i ON i.oid = c.conindid
            JOIN pg_namespace n ON n.oid = c.connamespace
            JOIN pg_index ix ON ix.indexrelid = c.conindid
            LEFT JOIN pg_attribute a
                   ON a.attrelid = i.oid AND a.attnum > ix.indnkeyatts
            WHERE n.nspname = @schema
              AND c.conname = @name
            ORDER BY a.attnum;
            """;

        var parameters = new[]
        {
            new DatabaseParameter<string>("@schema", schema),
            new DatabaseParameter<string>("@name", constraintName),
        };

        var included = new List<SqlName>();
        string? storageParameters = null;

        await using var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            // The LEFT JOIN yields one row with a null column when there are no INCLUDE
            // columns, so the storage parameters are still read from it.
            storageParameters ??= reader.IsDBNull("storage_parameters")
                ? null
                : reader.GetString("storage_parameters");

            if (!reader.IsDBNull("column_name"))
            {
                included.Add(tableSqlName.Child(reader.GetString("column_name")));
            }
        }

        return (included, storageParameters);
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
            var (includedColumns, storageParameters) = await ExtractConstraintIndexFacetsAsync(
                table.Schema, constraintName, tableSqlName, cancellationToken);

            model.Elements.Add(PostgresModelFactory.CreateUniqueConstraint(
                SqlName.Object(constraintName), tableSqlName, constraints[constraintName],
                table.Schema, includedColumns, storageParameters));
        }
    }

    /// <summary>
    /// Extracts the table's EXCLUDE constraints (issue #212).
    ///
    /// Read from <c>pg_constraint</c> joined to the index backing it, not from
    /// <c>pg_get_constraintdef</c>: measured, that function's output depends on the session
    /// <c>search_path</c> (an operator resolvable through it is rendered unqualified, and the
    /// same constraint therefore renders two ways in two sessions), so it cannot be the source
    /// of an element's identity. The structured columns are stable.
    ///
    /// <c>pg_get_indexdef</c> is likewise not usable as a whole: measured, it drops the
    /// operators entirely, rendering <c>EXCLUDE (a WITH =, b WITH =)</c> as
    /// <c>USING btree (a, b)</c>. Only its per-column form is used, and only for expression
    /// keys, exactly as the index path uses it.
    /// </summary>
    private async Task ExtractExclusionConstraintsAsync(Model model, TableRef table,
        CancellationToken cancellationToken = default)
    {
        var tableSqlName = SqlName.Object(table.BareName);

        // contype = 'x' is an exclusion constraint. The key columns are indkey positions
        // 1..indnkeyatts -- slicing there is what keeps an INCLUDE column from being read as a
        // key -- and conexclop is the parallel array of one operator per key, so the lateral
        // ordinality lines the two up.
        //
        // indkey is an int2vector, which unlike a normal Postgres array is 0-based, so the
        // key slice is [0:indnkeyatts-1]; conexclop is an ordinary 1-based array, hence the
        // unshifted subscript on it and the -1 on indoption / indclass / indcollation.
        //
        // The operator is qualified only when it does not live in pg_catalog, which is the rule
        // PostgreSQL itself applies when it reports one back -- measured, `OPERATOR(pg_catalog.=)`
        // comes back as a bare `=` while an operator in another schema keeps its qualifier.
        const string sql = """
            SELECT
                c.conname AS constraint_name,
                am.amname AS index_method,
                pg_get_expr(ix.indpred, ix.indrelid) AS filter_predicate,
                c.condeferrable AS is_deferrable,
                c.condeferred AS is_initially_deferred,
                array_to_string(i.reloptions, ', ') AS storage_parameters,
                k.ordinality AS column_ordinal,
                a.attname AS column_name,
                pg_get_indexdef(ix.indexrelid, k.ordinality::integer, true) AS key_expression,
                ix.indoption[k.ordinality - 1] AS column_option,
                CASE WHEN oc.opcdefault AND ia.attoptions IS NULL THEN NULL
                     ELSE oc.opcname END AS operator_class,
                array_to_string(ia.attoptions, ', ') AS operator_class_parameters,
                CASE WHEN ix.indcollation[k.ordinality - 1] <> ty.typcollation
                     THEN co.collname END AS collation_name,
                CASE WHEN opn.nspname = 'pg_catalog' THEN op.oprname
                     ELSE opn.nspname || '.' || op.oprname END AS exclusion_operator
            FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_index ix ON ix.indexrelid = c.conindid
            JOIN pg_class i ON i.oid = c.conindid
            JOIN pg_am am ON am.oid = i.relam
            JOIN LATERAL unnest(ix.indkey[0:ix.indnkeyatts - 1])
                WITH ORDINALITY AS k(attnum, ordinality) ON TRUE
            LEFT JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
            LEFT JOIN pg_type ty ON ty.oid = a.atttypid
            LEFT JOIN pg_opclass oc ON oc.oid = ix.indclass[k.ordinality - 1]
            LEFT JOIN pg_attribute ia
              ON ia.attrelid = ix.indexrelid AND ia.attnum = k.ordinality
            LEFT JOIN pg_collation co ON co.oid = ix.indcollation[k.ordinality - 1]
            LEFT JOIN pg_operator op ON op.oid = c.conexclop[k.ordinality]
            LEFT JOIN pg_namespace opn ON opn.oid = op.oprnamespace
            WHERE n.nspname = @schema
              AND t.relname = @name
              AND c.contype = 'x'
            ORDER BY c.conname, k.ordinality;
            """;

        var parameters = new[]
        {
            new DatabaseParameter<string>("@schema", table.Schema),
            new DatabaseParameter<string>("@name", table.BareName),
        };

        var constraintRows = new Dictionary<string, ExclusionConstraintRow>();
        var order = new List<string>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var constraintName = reader.GetString("constraint_name");

                if (!constraintRows.TryGetValue(constraintName, out var entry))
                {
                    // pg_get_expr returns NULL for a constraint with no WHERE clause.
                    var filterPredicate = reader.IsDBNull("filter_predicate")
                        ? null
                        : reader.GetString("filter_predicate");

                    // reloptions is NULL for an index with no WITH clause; array_to_string of
                    // an empty array is an empty string, so both mean "none".
                    var storageParameters = reader.IsDBNull("storage_parameters")
                        ? null
                        : reader.GetString("storage_parameters") is { Length: > 0 } sp ? sp : null;

                    entry = new ExclusionConstraintRow(
                        reader.GetString("index_method"),
                        filterPredicate,
                        storageParameters,
                        reader.GetBoolean("is_deferrable"),
                        reader.GetBoolean("is_initially_deferred"));

                    constraintRows.Add(constraintName, entry);
                    order.Add(constraintName);
                }

                // NULL for an expression key, whose indkey entry is 0 and so matches no column.
                var columnName = reader.IsDBNull("column_name")
                    ? null
                    : reader.GetString("column_name");

                var keyExpression = columnName is null
                    ? reader.GetString("key_expression")
                    : null;

                // Only btree carries per-key ASC/DESC and NULLS ordering; other access methods
                // report indoption 0 throughout, so surfacing an ordering for them would put
                // into the model something the emitted DDL cannot legally carry.
                bool? isAscending = null;
                bool? nullsFirst = null;

                if (entry.IndexMethod == "btree")
                {
                    // indoption bit 0x01 = DESC; bit 0x02 = NULLS FIRST.
                    var columnOption = reader.GetFieldValue<short>("column_option");
                    isAscending = (columnOption & 0x01) == 0;
                    nullsFirst = (columnOption & 0x02) != 0;
                }

                var key = new PostgresModelFactory.IndexedColumn(
                    // An expression key names no column, so the constraint's own name stands in
                    // to give the spec a stable identity, matching the parser builder.
                    columnName is not null
                        ? tableSqlName.Child(columnName)
                        : SqlName.Object(constraintName),
                    IsAscending: isAscending,
                    NullsFirst: nullsFirst,
                    OperatorClass: reader.IsDBNull("operator_class")
                        ? null
                        : reader.GetString("operator_class"),
                    OperatorClassParameters: reader.IsDBNull("operator_class_parameters")
                        ? null
                        : reader.GetString("operator_class_parameters"),
                    Collation: reader.IsDBNull("collation_name")
                        ? null
                        : reader.GetString("collation_name"),
                    KeyExpression: keyExpression);

                entry.Elements.Add(PostgresModelFactory.CreateExclusionConstraintElement(
                    key, reader.GetString("exclusion_operator")));
            }
        }

        foreach (var constraintName in order)
        {
            var entry = constraintRows[constraintName];

            // The INCLUDE columns live past indnkeyatts on the backing index, which is where a
            // primary key's and a unique constraint's are read from too. The storage parameters
            // that helper also returns are already read above, so only the columns are taken.
            var (includedColumns, _) = await ExtractConstraintIndexFacetsAsync(
                table.Schema, constraintName, tableSqlName, cancellationToken);

            model.Elements.Add(PostgresModelFactory.CreateExclusionConstraint(
                SqlName.Object(constraintName),
                tableSqlName,
                entry.IndexMethod,
                entry.Elements,
                table.Schema,
                entry.FilterPredicate,
                includedColumns,
                entry.StorageParameters,
                entry.IsDeferrable,
                entry.IsInitiallyDeferred));
        }
    }

    // The per-constraint facets of an EXCLUDE gathered while its element rows are read, before
    // the whole constraint becomes one element.
    private sealed record ExclusionConstraintRow(
        string IndexMethod,
        string? FilterPredicate,
        string? StorageParameters,
        bool IsDeferrable,
        bool IsInitiallyDeferred)
    {
        public List<Element> Elements { get; } = [];
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
                   pg_get_constraintdef(c.oid) AS constraint_def,
                   -- NO INHERIT (issue #205). Read as a boolean rather than parsed out of the
                   -- definition text, which renders it as a suffix outside the predicate's
                   -- parentheses (`CHECK ((b > 0)) NO INHERIT`).
                   c.connoinherit AS is_no_inherit
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

        var checks = new List<(string Name, string Definition, bool IsNoInherit)>();

        await using (var reader = await _database.RunScriptReaderAsync(sql, parameters, cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                checks.Add((reader.GetString("constraint_name"),
                    reader.GetString("constraint_def"),
                    reader.GetBoolean("is_no_inherit")));
            }
        }

        foreach (var (name, definition, isNoInherit) in checks)
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
                table.Schema, isNoInherit));
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
                pg_get_expr(ad.adbin, ad.adrelid) AS generation_expression,
                -- A column-level COLLATE (issue #159). attcollation is a resolved oid for every
                -- collatable column — 100 ("default") when none was declared — and 0 for a
                -- non-collatable type, while typcollation is the type's own default. Reporting
                -- the name only when the two differ is what distinguishes a declared collation
                -- from the implicit one; returning it unconditionally would make every text
                -- column re-diff on every deploy (measured against postgres:latest).
                CASE WHEN a.attcollation <> col_type.typcollation
                     THEN (SELECT collname FROM pg_collation WHERE oid = a.attcollation)
                END AS collation_name
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

            // Emitted last, matching the parser builder's property order (the Merkle hash is
            // order-sensitive). Non-null only for a collation that is not the type's default.
            if (!reader.IsDBNull(reader.GetOrdinal("collation_name")))
            {
                column.Properties.Add(new Property(
                    PostgresPropertyNames.Collation, reader.GetString("collation_name")));
            }

            columns.Entries.Add(column);
        }
    }
}
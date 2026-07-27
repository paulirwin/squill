using Squill.Core;
using Squill.PostgresParser.Syntax;

namespace Squill.Provider.Postgres;

/// <summary>
/// Owns the shape of every PostgreSQL model element. Both the parser-based and the
/// database-extraction model builders construct elements through this factory, so
/// the two representations agree by construction rather than by hand — a
/// prerequisite for comparing a parsed model against an extracted one.
/// </summary>
public static class PostgresModelFactory
{
    /// <summary>
    /// Builds a Postgres schema (namespace) element. A schema is a top-level, standalone,
    /// declared object identified by its name — Squill never creates one implicitly, so it
    /// is modeled and deployed like a table or extension. Its objects reference it by name
    /// via their own Schema relationship.
    /// </summary>
    public static Element CreateSchema(SqlName name)
        => new(PostgresElementTypes.SqlSchema)
        {
            Name = name,
        };

    /// <summary>
    /// Reads the schema (namespace) an element belongs to from its Schema relationship, or
    /// returns <c>null</c> when it has none. Centralized here — the sole owner of element
    /// shape — so the diff and the script generator agree on how a schema is stored.
    /// </summary>
    public static string? GetSchema(Element element)
        => element.GetRelationship(PostgresRelationshipNames.Schema)
            ?.Entries.OfType<Reference>().FirstOrDefault()?.Name;

    /// <summary>
    /// Records that a column is generated (computed): <c>GENERATED ALWAYS AS (expr) STORED</c>
    /// (issue #120). Called by both model builders so a parsed column and an extracted one
    /// carry the same properties in the same order (the Merkle hash is order-sensitive).
    ///
    /// The expression is carried for scripting only and does not take part in comparison:
    /// PostgreSQL rewrites the expression it is given (adding parentheses and type casts, so
    /// <c>price * quantity</c> comes back as <c>(price * (quantity)::numeric)</c>), so a
    /// declared expression could never hash-match one read back through
    /// <c>pg_get_expr</c> — the same treatment a domain's CHECK and a view's query get.
    /// What does participate is <em>that</em> the column is generated, which is a real
    /// structural difference: a generated column cannot be written to.
    /// </summary>
    public static void AddGeneratedColumnProperties(Element column, string? generationExpression)
    {
        // PostgreSQL has only STORED generated columns, but IsStored is recorded explicitly
        // so the property set matches MariaDB's (which also has VIRTUAL) and so scripting
        // never has to infer the storage kind.
        column.Properties.Add(new Property(PostgresPropertyNames.IsStored, true));

        if (generationExpression is not null)
        {
            column.Properties.Add(
                new Property(PostgresPropertyNames.GeneratedExpression, generationExpression,
                    participatesInIdentity: false));
        }
    }

    public static Element CreateTable(SqlName name, string schema)
        => new(PostgresElementTypes.SqlTable)
        {
            Name = name,
            Relationships =
            {
                new Relationship(PostgresRelationshipNames.Schema)
                {
                    new Reference(schema) { ExternalSource = "BuiltIns" }
                }
            }
        };

    /// <summary>
    /// Describes an indexed column: its canonical reference plus optional ordering and
    /// operator class (opclass, per PostgreSQL's CREATE INDEX). Null direction/nullsFirst
    /// mean "unspecified" and are omitted from the model; a null operator class means the
    /// type's default opclass, likewise omitted.
    /// </summary>
    public readonly record struct IndexedColumn(
        SqlName Column,
        bool? IsAscending = null,
        bool? NullsFirst = null,
        string? OperatorClass = null);

    public static Element CreateIndexedColumnSpecification(IndexedColumn column)
    {
        var element = new Element(PostgresElementTypes.SqlIndexedColumnSpecification)
        {
            Relationships =
            {
                new Relationship(PostgresRelationshipNames.Column)
                {
                    new Reference(column.Column)
                }
            }
        };

        if (column.IsAscending is bool isAscending)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.IsAscending, isAscending));
        }

        if (column.NullsFirst is bool nullsFirst)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.NullsFirst, nullsFirst));
        }

        // A non-default operator class (e.g. vector_cosine_ops on an HNSW index) is
        // stored so parsed and extracted models agree; the default opclass is omitted.
        if (column.OperatorClass is { } operatorClass)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.OperatorClass, operatorClass));
        }

        return element;
    }

    public static Element CreatePrimaryKey(SqlName name, SqlName definingTable, IEnumerable<IndexedColumn> columns)
    {
        var columnSpecs = new Relationship(PostgresRelationshipNames.ColumnSpecifications);

        foreach (var column in columns)
        {
            columnSpecs.Add(CreateIndexedColumnSpecification(column));
        }

        return new Element(PostgresElementTypes.SqlPrimaryKeyConstraint)
        {
            Name = name,
            Relationships =
            {
                columnSpecs,
                new Relationship(PostgresRelationshipNames.DefiningTable)
                {
                    new Reference(definingTable)
                }
            }
        };
    }

    /// <summary>
    /// A UNIQUE constraint on a table. Shaped like a primary key (an ordered column set
    /// owned by a defining table) because Postgres backs both with an index and records
    /// both in pg_constraint, where either can back a foreign key.
    /// </summary>
    public static Element CreateUniqueConstraint(
        SqlName name, SqlName definingTable, IEnumerable<IndexedColumn> columns,
        string schema = "public")
    {
        var columnSpecs = new Relationship(PostgresRelationshipNames.ColumnSpecifications);

        foreach (var column in columns)
        {
            columnSpecs.Add(CreateIndexedColumnSpecification(column));
        }

        return new Element(PostgresElementTypes.SqlUniqueConstraint)
        {
            Name = name,
            Relationships =
            {
                columnSpecs,
                new Relationship(PostgresRelationshipNames.DefiningTable)
                {
                    new Reference(definingTable)
                },
                // A unique constraint lives in its table's schema. Carrying it (as an index
                // does) lets ALTER TABLE ... ADD/DROP CONSTRAINT qualify the table correctly
                // instead of resolving it against the session search_path.
                new Relationship(PostgresRelationshipNames.Schema)
                {
                    new Reference(schema) { ExternalSource = "BuiltIns" }
                }
            }
        };
    }

    /// <summary>
    /// A CHECK constraint on a table (issue #120). Unlike a PK or UNIQUE it has no column
    /// set of its own — the predicate may reference any columns of the table (or none) — so
    /// the element carries the predicate text and its defining table, and nothing else.
    ///
    /// The predicate does not take part in comparison, for the same reason a domain's CHECK
    /// does not: PostgreSQL rewrites it when it stores it, so the declared
    /// <c>price &gt; 0</c> comes back from pg_get_constraintdef as <c>((price &gt; (0)::numeric))</c>.
    /// A CHECK constraint's modeled identity is its name and table instead, which is why an
    /// unnamed one is given the engine-derived <c>&lt;table&gt;_&lt;column&gt;_check</c> name.
    /// </summary>
    public static Element CreateCheckConstraint(
        SqlName name, SqlName definingTable, string checkExpression, string schema = "public")
        => new(PostgresElementTypes.SqlCheckConstraint)
        {
            Name = name,
            Properties =
            {
                new Property(PostgresPropertyNames.CheckExpression, checkExpression,
                    participatesInIdentity: false),
            },
            Relationships =
            {
                new Relationship(PostgresRelationshipNames.DefiningTable)
                {
                    new Reference(definingTable)
                },
                // Carried so ALTER TABLE ... ADD/DROP CONSTRAINT can qualify the table
                // rather than resolving it against the session search_path.
                new Relationship(PostgresRelationshipNames.Schema)
                {
                    new Reference(schema) { ExternalSource = "BuiltIns" }
                }
            }
        };

    public static Element CreateIndex(SqlName name,
        SqlName indexedObject,
        bool isUnique,
        string? indexMethod,
        IEnumerable<IndexedColumn> columns,
        string? filterPredicate = null,
        string? storageParameters = null,
        string schema = "public")
    {
        var columnSpecs = new Relationship(PostgresRelationshipNames.ColumnSpecifications);

        foreach (var column in columns)
        {
            columnSpecs.Add(CreateIndexedColumnSpecification(column));
        }

        var element = new Element(PostgresElementTypes.SqlIndex)
        {
            Name = name,
            Relationships =
            {
                columnSpecs,
                new Relationship(PostgresRelationshipNames.IndexedObject)
                {
                    new Reference(indexedObject)
                },
                // An index lives in its table's schema. Carrying it (like a table's Schema
                // relationship) lets DROP INDEX qualify correctly and keeps the parser and
                // DB-extraction builders agreeing for non-public schemas.
                new Relationship(PostgresRelationshipNames.Schema)
                {
                    new Reference(schema) { ExternalSource = "BuiltIns" }
                }
            }
        };

        element.Properties.Add(new Property(PostgresPropertyNames.IsUnique, isUnique));

        if (indexMethod is not null)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.IndexMethod, indexMethod));
        }

        // A filter predicate (WHERE clause) marks this a partial index. Absent for a
        // full index so parsed and extracted models hash-match when there's no filter.
        if (filterPredicate is not null)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.FilterPredicate, filterPredicate));
        }

        // WITH (...) storage parameters (e.g. HNSW's m / ef_construction), stored as a
        // canonical "name=value, name=value" string. Absent when the index declares no
        // storage parameters, so parsed and extracted models hash-match in that case.
        if (storageParameters is not null)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.StorageParameters, storageParameters));
        }

        return element;
    }

    /// <summary>
    /// Builds a Postgres extension element. An extension is a top-level, standalone
    /// object identified by its name; it is not dependent on any table. Version is
    /// optional: it is only stored when explicitly declared, so a parsed model (which
    /// usually omits the version) hash-matches one extracted from the database (whose
    /// installed version is not part of the desired-state identity).
    /// </summary>
    public static Element CreateExtension(SqlName name, string? version = null)
    {
        var element = new Element(PostgresElementTypes.SqlExtension)
        {
            Name = name,
        };

        if (version is not null)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.Version, version));
        }

        return element;
    }

    /// <summary>
    /// Builds a standalone sequence element (issue #122) — <c>CREATE SEQUENCE name [options]</c>.
    ///
    /// Only options that differ from the PostgreSQL default are stored, and the defaults are
    /// resolved against the sequence's own type and direction (see
    /// <see cref="PostgresIdentitySequenceDefaults"/>). This is what lets a parsed model
    /// hash-match one extracted from <c>pg_sequence</c>, which always reports every option with
    /// its defaults filled in — the same omit-when-default convention identity columns use.
    ///
    /// Pass the values exactly as declared (or as extracted); this method decides what to keep.
    /// </summary>
    public static Element CreateSequence(SqlName name, string schema,
        string? dataTypeName, long? startValue, long? increment, long? minValue, long? maxValue,
        long? cacheSize, bool? isCycling)
    {
        var element = new Element(PostgresElementTypes.SqlSequence)
        {
            Name = name,
            Relationships =
            {
                new Relationship(PostgresRelationshipNames.Schema)
                {
                    new Reference(schema) { ExternalSource = "BuiltIns" },
                },
            },
        };

        // The type governs the default bounds, so it is resolved first and used below even
        // when it is itself left at the default and therefore not stored.
        var typeName = dataTypeName ?? PostgresIdentitySequenceDefaults.DefaultSequenceTypeName;

        if (!string.Equals(typeName, PostgresIdentitySequenceDefaults.DefaultSequenceTypeName,
                StringComparison.Ordinal))
        {
            element.Properties.Add(new Property(PostgresPropertyNames.SequenceDataType, typeName));
        }

        var effectiveIncrement = increment ?? PostgresIdentitySequenceDefaults.Increment;

        var (defaultStart, defaultMin, defaultMax) =
            PostgresIdentitySequenceDefaults.For(typeName, effectiveIncrement);

        AddIfNotDefault(element, PostgresPropertyNames.Increment,
            effectiveIncrement, PostgresIdentitySequenceDefaults.Increment);
        AddIfNotDefault(element, PostgresPropertyNames.MinValue, minValue ?? defaultMin, defaultMin);
        AddIfNotDefault(element, PostgresPropertyNames.MaxValue, maxValue ?? defaultMax, defaultMax);
        AddIfNotDefault(element, PostgresPropertyNames.StartValue,
            startValue ?? defaultStart, defaultStart);
        AddIfNotDefault(element, PostgresPropertyNames.CacheSize,
            cacheSize ?? PostgresIdentitySequenceDefaults.CacheSize,
            PostgresIdentitySequenceDefaults.CacheSize);

        if ((isCycling ?? PostgresIdentitySequenceDefaults.IsCycling)
            != PostgresIdentitySequenceDefaults.IsCycling)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.IsCycling, true));
        }

        return element;
    }

    private static void AddIfNotDefault(Element element, string propertyName,
        long value, long defaultValue)
    {
        if (value != defaultValue)
        {
            element.Properties.Add(new Property(propertyName, value));
        }
    }

    /// <summary>
    /// Builds an enum-type element (issue #75) — <c>CREATE TYPE name AS ENUM (...)</c>. An enum
    /// is a top-level, standalone, declared object. Its labels are stored in declaration order
    /// (their significant sort order) as a canonical comma-joined, single-quoted string, so the
    /// property hashes stably and the same text is available for scripting.
    /// </summary>
    public static Element CreateEnumType(SqlName name, string schema, IReadOnlyList<string> labels)
    {
        var element = new Element(PostgresElementTypes.SqlEnumType)
        {
            Name = name,
            Relationships =
            {
                new Relationship(PostgresRelationshipNames.Schema)
                {
                    new Reference(schema) { ExternalSource = "BuiltIns" },
                },
            },
        };

        element.Properties.Add(new Property(PostgresPropertyNames.Labels, RenderEnumLabels(labels)));

        return element;
    }

    /// <summary>
    /// Reads an enum type's labels, in order, from its <see cref="PostgresPropertyNames.Labels"/>
    /// property. Centralized here so the model builders and the script generator agree.
    /// </summary>
    public static IReadOnlyList<string> GetEnumLabels(Element element)
    {
        var rendered = element.GetProperty<string>(PostgresPropertyNames.Labels);

        return rendered is null ? [] : ParseEnumLabels(rendered);
    }

    // 'G', 'PG-13'  ->  the canonical stored form. A single-quote in a label is doubled per
    // PostgreSQL's string-literal escaping.
    private static string RenderEnumLabels(IEnumerable<string> labels)
        => string.Join(", ", labels.Select(l => $"'{l.Replace("'", "''")}'"));

    private static IReadOnlyList<string> ParseEnumLabels(string rendered)
    {
        var result = new List<string>();

        foreach (var part in rendered.Split(", "))
        {
            var trimmed = part.Trim();

            if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
            {
                result.Add(trimmed[1..^1].Replace("''", "'"));
            }
            else
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    /// <summary>
    /// Builds a domain element (issue #75) — <c>CREATE DOMAIN name AS &lt;type&gt; [CHECK ...]</c>.
    /// A domain is a top-level, standalone, declared object. Its base type is carried as a
    /// <see cref="PostgresRelationshipNames.TypeSpecifier"/> relationship (the same shape a column
    /// uses) and its CHECK expression, if any, as a canonical text property.
    /// </summary>
    public static Element CreateDomain(SqlName name, string schema, Element typeSpecifier,
        string? checkExpression)
    {
        var element = new Element(PostgresElementTypes.SqlDomain)
        {
            Name = name,
            Relationships =
            {
                new Relationship(PostgresRelationshipNames.Schema)
                {
                    new Reference(schema) { ExternalSource = "BuiltIns" },
                },
                new Relationship(PostgresRelationshipNames.TypeSpecifier) { typeSpecifier },
            },
        };

        if (checkExpression is not null)
        {
            // The CHECK expression is carried for scripting only and does not take part in
            // comparison: PostgreSQL rewrites the predicate it is given (e.g. adds
            // parentheses, so `VALUE >= 1901 AND VALUE <= 2155` comes back as
            // `((VALUE >= 1901) AND (VALUE <= 2155))`), so a declared expression can never
            // hash-match one extracted from pg_get_constraintdef. A domain's modeled
            // identity is its name and base type instead — mirroring how a view's query is
            // handled (see Property.ParticipatesInIdentity).
            element.Properties.Add(
                new Property(PostgresPropertyNames.CheckExpression, checkExpression,
                    participatesInIdentity: false));
        }

        return element;
    }

    /// <summary>
    /// A procedure parameter as it appears in the routine's declaration: its mode, optional
    /// name, the type exactly as written, and any DEFAULT expression. This is what the
    /// procedure is scripted from; identity comes from the normalized argument types.
    /// </summary>
    /// <param name="Mode">IN, INOUT, OUT or VARIADIC, spelled as PostgreSQL writes it.</param>
    /// <param name="Name">The parameter name, or null when the parameter is unnamed.</param>
    /// <param name="Type">
    /// The PostgreSQL-normalized type name with modifiers discarded (e.g. <c>character
    /// varying</c>), which is all the catalog retains for a routine parameter.
    /// </param>
    public readonly record struct ProcedureParameter(
        string Mode,
        string? Name,
        string Type);

    /// <summary>
    /// Builds a stored procedure element.
    ///
    /// PostgreSQL allows overloading — <c>p(integer)</c> and <c>p(text)</c> are distinct
    /// procedures — but the schema comparison identifies an element by its type, name and
    /// schema alone. The argument signature is therefore folded into the name (as
    /// <c>schema.name(type,type)</c>) so overloads never collide, and the bare name and
    /// argument list are kept as properties for scripting.
    ///
    /// <paramref name="argumentTypes"/> must be the PostgreSQL-normalized type names (e.g.
    /// <c>character varying</c>, not <c>varchar(10)</c>) so a parsed model hash-matches one
    /// extracted from a live database, which reads them back from pg_proc.
    /// </summary>
    public static Element CreateProcedure(
        string schema,
        string routineName,
        string argumentTypes,
        string language,
        string body,
        IEnumerable<ProcedureParameter> parameters,
        bool isSecurityDefiner = false)
    {
        var element = new Element(PostgresElementTypes.SqlProcedure)
        {
            Name = SqlName.Object(schema, $"{routineName}({argumentTypes})"),
            Relationships =
            {
                new Relationship(PostgresRelationshipNames.Schema)
                {
                    new Reference(schema) { ExternalSource = "BuiltIns" }
                }
            },
            Properties =
            {
                new Property(PostgresPropertyNames.RoutineName, routineName),
                new Property(PostgresPropertyNames.ArgumentTypes, argumentTypes),
                new Property(PostgresPropertyNames.Arguments, RenderParameters(parameters)),
                new Property(PostgresPropertyNames.Language, language),
                new Property(PostgresPropertyNames.Body, body),
            },
        };

        // INVOKER is the PostgreSQL default, so only DEFINER is stored — keeping the
        // element's shape identical to the extracted one for the common case.
        if (isSecurityDefiner)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.IsSecurityDefiner, true));
        }

        return element;
    }

    /// <summary>
    /// Builds a function element (issue #81).
    ///
    /// Like <see cref="CreateProcedure"/>, overloads are kept distinct by folding the
    /// argument signature into the Name (<c>schema.name(type,type)</c>), and the arguments,
    /// language and body are stored for scripting. A function additionally carries its return
    /// type (<paramref name="returnType"/>, the canonical name pg_proc reports) and whether it
    /// is set-returning. Volatility and strictness are stored only when they differ from the
    /// PostgreSQL defaults (VOLATILE, CALLED ON NULL INPUT), so a parsed model whose source
    /// omits them hash-matches one extracted from the database.
    /// </summary>
    public static Element CreateFunction(
        string schema,
        string routineName,
        string argumentTypes,
        string returnType,
        bool returnsSet,
        string language,
        string body,
        IEnumerable<ProcedureParameter> parameters,
        string? volatility = null,
        bool isStrict = false,
        bool isSecurityDefiner = false)
    {
        var element = new Element(PostgresElementTypes.SqlFunction)
        {
            Name = SqlName.Object(schema, $"{routineName}({argumentTypes})"),
            Relationships =
            {
                new Relationship(PostgresRelationshipNames.Schema)
                {
                    new Reference(schema) { ExternalSource = "BuiltIns" }
                }
            },
            Properties =
            {
                new Property(PostgresPropertyNames.RoutineName, routineName),
                new Property(PostgresPropertyNames.ArgumentTypes, argumentTypes),
                new Property(PostgresPropertyNames.Arguments, RenderParameters(parameters)),
                new Property(PostgresPropertyNames.ReturnType, returnType),
                new Property(PostgresPropertyNames.Language, language),
                new Property(PostgresPropertyNames.Body, body),
            },
        };

        // Only the non-default facets are stored so a parsed model matches the extracted one.
        if (returnsSet)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.ReturnsSet, true));
        }

        // VOLATILE is the PostgreSQL default, so it is stored as an absent property.
        if (volatility is not null && !string.Equals(volatility, "VOLATILE", StringComparison.Ordinal))
        {
            element.Properties.Add(new Property(PostgresPropertyNames.Volatility, volatility));
        }

        if (isStrict)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.IsStrict, true));
        }

        if (isSecurityDefiner)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.IsSecurityDefiner, true));
        }

        return element;
    }

    /// <summary>
    /// Builds an aggregate element (issue #82).
    ///
    /// Like a function, overloads are kept distinct by folding the input-type signature into
    /// the Name (<c>schema.name(type,type)</c>), and the arguments are stored for scripting.
    /// An aggregate additionally carries its state transition function
    /// (<paramref name="stateFunction"/>, the SFUNC) and state type
    /// (<paramref name="stateType"/>, the STYPE). The SFUNC name is schema-qualified as
    /// pg_proc reports it and the STYPE is the canonical type name (format_type), so a parsed
    /// model hash-matches one extracted from a live database.
    /// </summary>
    public static Element CreateAggregate(
        string schema,
        string routineName,
        string argumentTypes,
        string stateFunction,
        string stateType,
        IEnumerable<ProcedureParameter> parameters)
    {
        return new Element(PostgresElementTypes.SqlAggregate)
        {
            Name = SqlName.Object(schema, $"{routineName}({argumentTypes})"),
            Relationships =
            {
                new Relationship(PostgresRelationshipNames.Schema)
                {
                    new Reference(schema) { ExternalSource = "BuiltIns" }
                }
            },
            Properties =
            {
                new Property(PostgresPropertyNames.RoutineName, routineName),
                new Property(PostgresPropertyNames.ArgumentTypes, argumentTypes),
                new Property(PostgresPropertyNames.Arguments, RenderParameters(parameters)),
                new Property(PostgresPropertyNames.StateFunction, stateFunction),
                new Property(PostgresPropertyNames.StateType, stateType),
            },
        };
    }

    /// <summary>
    /// Builds a trigger element (issue #83).
    ///
    /// A trigger's identity is its name (scoped to the table, so folded into the element Name
    /// as <c>schema.table.trigger</c> to keep same-named triggers on different tables
    /// distinct), the table it fires on, and its behavior facets: <paramref name="timing"/>
    /// (BEFORE/AFTER/INSTEAD OF), <paramref name="events"/> (the canonical OR'd event list),
    /// <paramref name="level"/> (ROW/STATEMENT) and the function it executes
    /// (<paramref name="triggerFunction"/>, schema-qualified) with its literal
    /// <paramref name="functionArguments"/>. All of these round-trip faithfully through
    /// <c>pg_get_triggerdef</c>, so a parsed model hash-matches one extracted from a live
    /// database. The trigger carries its table's <paramref name="schema"/> so it can be
    /// schema-scoped for identity and dependency ordering.
    /// </summary>
    public static Element CreateTrigger(
        string schema,
        string triggerName,
        SqlName table,
        string timing,
        string events,
        string level,
        string triggerFunction,
        string functionArguments)
    {
        return new Element(PostgresElementTypes.SqlTrigger)
        {
            Name = SqlName.Object(schema, $"{table.UnqualifiedName}.{triggerName}"),
            Relationships =
            {
                new Relationship(PostgresRelationshipNames.Schema)
                {
                    new Reference(schema) { ExternalSource = "BuiltIns" }
                },
                new Relationship(PostgresRelationshipNames.TriggerTable)
                {
                    new Reference(table)
                },
            },
            Properties =
            {
                new Property(PostgresPropertyNames.RoutineName, triggerName),
                new Property(PostgresPropertyNames.Timing, timing),
                new Property(PostgresPropertyNames.Events, events),
                new Property(PostgresPropertyNames.Level, level),
                new Property(PostgresPropertyNames.TriggerFunction, triggerFunction),
                new Property(PostgresPropertyNames.FunctionArguments, functionArguments),
            },
        };
    }

    /// <summary>
    /// Builds a view element (issue #42).
    ///
    /// A view's identity is its name and its ordered column list — the two facets
    /// PostgreSQL reports back faithfully. The query itself is stored as
    /// <see cref="PostgresPropertyNames.Definition"/> for scripting, but is excluded from
    /// comparison by
    /// <see cref="PostgresDatabaseDependencyAnalyzer.NormalizeForComparison"/>: PostgreSQL
    /// rewrites a view's query when it stores it (<c>pg_get_viewdef</c> reformats
    /// whitespace and layout), so a declared body could never hash-match an extracted one
    /// and would otherwise force a recreate on every single deploy.
    ///
    /// The trade-off this buys is deliberate: a changed query that leaves the column list
    /// untouched is not detected as a change. Adding, removing, renaming or reordering a
    /// column is.
    /// </summary>
    /// <param name="definition">
    /// The declared query, for scripting. Null when the element comes from a live database:
    /// PostgreSQL has only its own rewritten copy of the query, which would never match a
    /// declared one, so an extracted view carries no definition at all. That keeps both
    /// sides of a comparison hash-equal on the facets that do round-trip.
    /// </param>
    public static Element CreateView(
        SqlName name,
        string schema,
        IEnumerable<string> columnNames,
        string? definition)
    {
        var columns = new Relationship(PostgresRelationshipNames.Columns);

        foreach (var columnName in columnNames)
        {
            columns.Add(new Element(PostgresElementTypes.SqlViewColumn)
            {
                Name = name.Child(columnName),
            });
        }

        var element = new Element(PostgresElementTypes.SqlView)
        {
            Name = name,
            Relationships =
            {
                columns,
                new Relationship(PostgresRelationshipNames.Schema)
                {
                    new Reference(schema) { ExternalSource = "BuiltIns" }
                }
            },
        };

        if (definition is not null)
        {
            // Excluded from the element's identity: PostgreSQL rewrites a view's query when
            // it stores it, so this declared text could never match what the database
            // reports back. The view's name and column list carry its identity instead.
            element.Properties.Add(new Property(
                PostgresPropertyNames.Definition, definition, participatesInIdentity: false));
        }

        return element;
    }

    /// <summary>
    /// Renders a procedure's parameter list the way PostgreSQL's
    /// pg_get_function_arguments does, so the parsed and extracted models agree: mode
    /// first (IN is always written), then name, then type, then any DEFAULT.
    /// </summary>
    private static string RenderParameters(IEnumerable<ProcedureParameter> parameters)
        => string.Join(", ", parameters.Select(parameter => parameter.Name is { } name
            ? $"{parameter.Mode} {name} {parameter.Type}"
            : $"{parameter.Mode} {parameter.Type}"));

    /// <summary>
    /// Builds a foreign key constraint element. Referencing and referenced columns are
    /// ordered, canonical (table-qualified) references so a composite key's column
    /// pairing survives. NO ACTION is the Postgres default and is stored as an absent
    /// property so parsed and extracted models hash-match when no action is specified.
    /// </summary>
    public static Element CreateForeignKey(SqlName name,
        SqlName definingTable,
        IEnumerable<SqlName> columns,
        SqlName foreignTable,
        IEnumerable<SqlName> foreignColumns,
        ReferentialAction onDelete,
        ReferentialAction onUpdate)
    {
        var columnRelationship = new Relationship(PostgresRelationshipNames.ForeignKeyColumns);

        foreach (var column in columns)
        {
            columnRelationship.Add(new Reference(column));
        }

        var foreignColumnRelationship = new Relationship(PostgresRelationshipNames.ForeignColumns);

        foreach (var foreignColumn in foreignColumns)
        {
            foreignColumnRelationship.Add(new Reference(foreignColumn));
        }

        var element = new Element(PostgresElementTypes.SqlForeignKeyConstraint)
        {
            Name = name,
            Relationships =
            {
                columnRelationship,
                new Relationship(PostgresRelationshipNames.DefiningTable)
                {
                    new Reference(definingTable)
                },
                new Relationship(PostgresRelationshipNames.ForeignTable)
                {
                    new Reference(foreignTable)
                },
                foreignColumnRelationship,
            }
        };

        if (onDelete != ReferentialAction.NoAction)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.DeleteAction, onDelete.ToString()));
        }

        if (onUpdate != ReferentialAction.NoAction)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.UpdateAction, onUpdate.ToString()));
        }

        return element;
    }
}

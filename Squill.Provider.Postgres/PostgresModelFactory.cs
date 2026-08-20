using Squill.Core;
using Squill.PostgresParser;
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
    /// The raw expression is carried for scripting, so a deploy reproduces the spelling the user
    /// wrote. It cannot itself take part in comparison: PostgreSQL rewrites the expression it is
    /// given (adding parentheses and type casts, so <c>price * quantity</c> comes back as
    /// <c>(price * (quantity)::numeric)</c>), so a declared expression could never hash-match one
    /// read back through <c>pg_get_expr</c>. Its canonical form does instead (issue #156), which
    /// is what makes redefining the expression a change the deploy acts on.
    ///
    /// Also participating is <em>that</em> the column is generated, which is a real structural
    /// difference: a generated column cannot be written to.
    /// </summary>
    public static void AddGeneratedColumnProperties(Element column, string? generationExpression)
    {
        // PostgreSQL has only STORED generated columns, but IsStored is recorded explicitly
        // so the property set matches MariaDB's (which also has VIRTUAL) and so scripting
        // never has to infer the storage kind.
        column.Properties.Add(new Property(PostgresPropertyNames.IsStored, true));

        if (generationExpression is not null)
        {
            AddExpressionProperties(
                column,
                PostgresPropertyNames.GeneratedExpression,
                PostgresPropertyNames.NormalizedGeneratedExpression,
                generationExpression);
        }
    }

    /// <summary>
    /// Records an expression as the pair of properties comparison and scripting each need: the
    /// raw text exactly as given, and — when one can be derived — its canonical form (issue #156).
    ///
    /// Only the canonical form takes part in identity. When the expression contains a construct
    /// the normalizer cannot reduce, no canonical property is added and the raw one stays out of
    /// identity too, so the element falls back to the pre-#156 behaviour: a redefinition is
    /// missed, rather than an unchanged expression looking changed and redeploying forever.
    /// </summary>
    private static void AddExpressionProperties(
        Element element, string rawName, string normalizedName, string expression)
    {
        element.Properties.Add(new Property(rawName, expression, participatesInIdentity: false));

        if (ExpressionNormalizer.TryNormalize(expression, out var canonical))
        {
            element.Properties.Add(new Property(normalizedName, canonical));
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
    /// Describes an indexed column: its canonical reference plus optional ordering,
    /// operator class (opclass, per PostgreSQL's CREATE INDEX) and collation. Null
    /// direction/nullsFirst mean "unspecified" and are omitted from the model; a null operator
    /// class means the type's default opclass, and a null collation the column's own, likewise
    /// omitted.
    /// </summary>
    /// <remarks>
    /// An expression key (<c>CREATE INDEX ix ON people (lower(name))</c>) sets
    /// <paramref name="KeyExpression"/> instead of naming a column; <paramref name="Column"/>
    /// is then the index's own name, used only to give the spec a stable identity.
    /// </remarks>
    public readonly record struct IndexedColumn(
        SqlName Column,
        bool? IsAscending = null,
        bool? NullsFirst = null,
        string? OperatorClass = null,
        string? Collation = null,
        string? KeyExpression = null,
        string? OperatorClassParameters = null);

    public static Element CreateIndexedColumnSpecification(IndexedColumn column)
    {
        var element = new Element(PostgresElementTypes.SqlIndexedColumnSpecification);

        // An expression key is text rather than a reference to a column, so it replaces the
        // Column relationship instead of joining it (issue #160). The raw spelling is kept for
        // scripting but excluded from identity — PostgreSQL rewrites what it is given, so
        // lower(name) may come back parenthesized or cast — while the canonical form compares.
        if (column.KeyExpression is { } keyExpression)
        {
            AddExpressionProperties(
                element,
                PostgresPropertyNames.KeyExpression,
                PostgresPropertyNames.NormalizedKeyExpression,
                keyExpression);
        }
        else
        {
            element.Relationships.Add(new Relationship(PostgresRelationshipNames.Column)
            {
                new Reference(column.Column)
            });
        }

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

        // The parameters of a parameterized operator class (issue #211). Stored beside the
        // opclass rather than folded into it so the name stays comparable on its own, and
        // because the two are read from different catalog places: the name from pg_opclass,
        // the parameters from pg_attribute.attoptions on the index relation.
        if (column.OperatorClassParameters is { } operatorClassParameters)
        {
            element.Properties.Add(new Property(
                PostgresPropertyNames.OperatorClassParameters, operatorClassParameters));
        }

        // A per-key COLLATE (issue #160), stored only when it differs from the column type's
        // own collation. Measured: pg_index.indcollation reports a resolved collation ("default",
        // oid 100) for every collatable key column, so storing it unconditionally would make
        // every text index re-diff on every deploy — the same rule #159 applied to columns.
        if (column.Collation is { } collation)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.Collation, collation));
        }

        return element;
    }

    /// <summary>
    /// Adds the facets a constraint shares with the index that backs it (issue #210): the
    /// <c>INCLUDE (...)</c> covering columns and the <c>WITH (...)</c> storage parameters.
    ///
    /// Deliberately the same relationship and property an index uses, so the constraint
    /// spelling and the CREATE INDEX spelling of the same declaration converge on one
    /// representation instead of two that cannot be compared. Each is added only when present,
    /// so an ordinary constraint hashes exactly as it did before and cannot start re-diffing.
    ///
    /// Tablespace is not among them: the index path rejects any non-default one rather than
    /// modeling it (issue #160, measured), and the constraint path does the same.
    /// </summary>
    private static void AddIndexBackedConstraintFacets(
        Element element,
        IEnumerable<SqlName>? includedColumns,
        string? storageParameters)
    {
        if (includedColumns is not null)
        {
            var included = new Relationship(PostgresRelationshipNames.IncludedColumns);

            foreach (var includedColumn in includedColumns)
            {
                included.Add(new Reference(includedColumn));
            }

            if (included.Entries.Count > 0)
            {
                element.Relationships.Add(included);
            }
        }

        if (storageParameters is not null)
        {
            element.Properties.Add(
                new Property(PostgresPropertyNames.StorageParameters, storageParameters));
        }
    }

    public static Element CreatePrimaryKey(
        SqlName name, SqlName definingTable, IEnumerable<IndexedColumn> columns,
        string schema = "public",
        IEnumerable<SqlName>? includedColumns = null,
        string? storageParameters = null)
    {
        var columnSpecs = new Relationship(PostgresRelationshipNames.ColumnSpecifications);

        foreach (var column in columns)
        {
            columnSpecs.Add(CreateIndexedColumnSpecification(column));
        }

        var element = new Element(PostgresElementTypes.SqlPrimaryKeyConstraint)
        {
            Name = name,
            Relationships =
            {
                columnSpecs,
                new Relationship(PostgresRelationshipNames.DefiningTable)
                {
                    new Reference(definingTable)
                },
                // A primary key lives in its table's schema, and carries it for the same reason
                // a unique constraint does. It is also what tells two same-named keys apart:
                // Postgres names the primary key of both `public.orders` and `staging.orders`
                // `orders_pkey`, and the defining-table reference is a bare name, so without
                // the schema the two elements are indistinguishable and the compare matches one
                // against both (issue #200).
                new Relationship(PostgresRelationshipNames.Schema)
                {
                    new Reference(schema) { ExternalSource = "BuiltIns" }
                }
            }
        };

        AddIndexBackedConstraintFacets(element, includedColumns, storageParameters);

        return element;
    }

    /// <summary>
    /// A UNIQUE constraint on a table. Shaped like a primary key (an ordered column set
    /// owned by a defining table) because Postgres backs both with an index and records
    /// both in pg_constraint, where either can back a foreign key.
    /// </summary>
    public static Element CreateUniqueConstraint(
        SqlName name, SqlName definingTable, IEnumerable<IndexedColumn> columns,
        string schema = "public",
        IEnumerable<SqlName>? includedColumns = null,
        string? storageParameters = null)
    {
        var columnSpecs = new Relationship(PostgresRelationshipNames.ColumnSpecifications);

        foreach (var column in columns)
        {
            columnSpecs.Add(CreateIndexedColumnSpecification(column));
        }

        var element = new Element(PostgresElementTypes.SqlUniqueConstraint)
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

        AddIndexBackedConstraintFacets(element, includedColumns, storageParameters);

        return element;
    }

    /// <summary>
    /// A CHECK constraint on a table (issue #120). Unlike a PK or UNIQUE it has no column
    /// set of its own — the predicate may reference any columns of the table (or none) — so
    /// the element carries the predicate text and its defining table, and nothing else.
    ///
    /// The raw predicate cannot take part in comparison: PostgreSQL rewrites it when it stores
    /// it, so the declared <c>price &gt; 0</c> comes back from pg_get_constraintdef as
    /// <c>((price &gt; (0)::numeric))</c>. Its canonical form does instead (issue #156), so
    /// redefining the predicate under the same constraint name is a change the deploy acts on
    /// rather than a silent no-op. The constraint's name and table still identify it — which is
    /// why an unnamed one is given the engine-derived
    /// <c>&lt;table&gt;_&lt;column&gt;_check</c> name.
    /// </summary>
    public static Element CreateCheckConstraint(
        SqlName name, SqlName definingTable, string checkExpression, string schema = "public",
        bool isNoInherit = false)
    {
        var element = new Element(PostgresElementTypes.SqlCheckConstraint)
        {
            Name = name,
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

        AddExpressionProperties(
            element,
            PostgresPropertyNames.CheckExpression,
            PostgresPropertyNames.NormalizedCheckExpression,
            checkExpression);

        // Inheritable is the Postgres default (connoinherit = false), so the property is stored
        // only when the constraint is NO INHERIT (issue #205).
        if (isNoInherit)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.IsNoInherit, true));
        }

        return element;
    }

    /// <summary>
    /// One <c>key WITH operator</c> pair of an exclusion constraint (issue #212).
    ///
    /// The key half is a nested <see cref="PostgresElementTypes.SqlIndexedColumnSpecification"/>
    /// rather than being flattened into this element, so an exclusion key gets the ordering,
    /// operator class, collation and expression-versus-column handling an index key already
    /// has, instead of a second implementation that could drift from it.
    /// </summary>
    public static Element CreateExclusionConstraintElement(
        IndexedColumn key, string exclusionOperator)
    {
        var element = new Element(PostgresElementTypes.SqlExclusionConstraintElement)
        {
            Relationships =
            {
                new Relationship(PostgresRelationshipNames.ColumnSpecifications)
                {
                    CreateIndexedColumnSpecification(key),
                },
            },
        };

        element.Properties.Add(
            new Property(PostgresPropertyNames.ExclusionOperator, exclusionOperator));

        return element;
    }

    /// <summary>
    /// An EXCLUDE constraint on a table (issue #212):
    /// <c>EXCLUDE USING gist (room WITH =, during WITH &amp;&amp;)</c>.
    ///
    /// Backed by an index like a primary key or unique constraint, so it carries the same
    /// INCLUDE and WITH (...) facets. Unlike those, its elements pair each key with a
    /// comparison operator, and it accepts a WHERE predicate restricting which rows take part.
    /// </summary>
    /// <remarks>
    /// The access method is required rather than optional: measured, PostgreSQL reports one
    /// back for every exclusion constraint, so an omitted <c>USING</c> comes back as
    /// <c>btree</c>. Storing the absence would make every bare EXCLUDE differ from the same
    /// constraint read back out of the database, re-diffing on every deploy -- so the caller
    /// resolves the default instead.
    /// </remarks>
    public static Element CreateExclusionConstraint(
        SqlName name,
        SqlName definingTable,
        string indexMethod,
        IEnumerable<Element> exclusionElements,
        string schema = "public",
        string? filterPredicate = null,
        IEnumerable<SqlName>? includedColumns = null,
        string? storageParameters = null,
        bool isDeferrable = false,
        bool isInitiallyDeferred = false)
    {
        var elements = new Relationship(PostgresRelationshipNames.ExclusionElements);

        foreach (var exclusionElement in exclusionElements)
        {
            elements.Add(exclusionElement);
        }

        var element = new Element(PostgresElementTypes.SqlExclusionConstraint)
        {
            Name = name,
            Relationships =
            {
                elements,
                new Relationship(PostgresRelationshipNames.DefiningTable)
                {
                    new Reference(definingTable)
                },
                // Carried for the same reason a unique constraint carries it: it lets
                // ALTER TABLE ... ADD/DROP CONSTRAINT qualify the table rather than resolving
                // it against the session search_path, and it tells two same-named constraints
                // in different schemas apart.
                new Relationship(PostgresRelationshipNames.Schema)
                {
                    new Reference(schema) { ExternalSource = "BuiltIns" }
                }
            }
        };

        element.Properties.Add(new Property(PostgresPropertyNames.IndexMethod, indexMethod));

        // The WHERE predicate selects which rows participate; it rejects none itself. Stored
        // raw for scripting and canonically for comparison, because PostgreSQL rewrites what it
        // is given -- the same treatment a CHECK predicate and a partial index's filter get.
        if (filterPredicate is not null)
        {
            AddExpressionProperties(
                element,
                PostgresPropertyNames.FilterPredicate,
                PostgresPropertyNames.NormalizedFilterPredicate,
                filterPredicate);
        }

        AddIndexBackedConstraintFacets(element, includedColumns, storageParameters);

        // NOT DEFERRABLE INITIALLY IMMEDIATE is the Postgres default, so each flag is stored
        // only when true, matching what pg_constraint reports.
        if (isDeferrable)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.IsDeferrable, true));
        }

        if (isInitiallyDeferred)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.IsInitiallyDeferred, true));
        }

        return element;
    }

    public static Element CreateIndex(SqlName name,
        SqlName indexedObject,
        bool isUnique,
        string? indexMethod,
        IEnumerable<IndexedColumn> columns,
        string? filterPredicate = null,
        string? storageParameters = null,
        string schema = "public",
        IEnumerable<SqlName>? includedColumns = null,
        bool nullsNotDistinct = false)
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

        // INCLUDE (...) covering columns (issue #160). A separate relationship from the key
        // columns, because they are stored in the index without being part of its key: they
        // carry no ordering or opclass, and on a unique index they take no part in uniqueness.
        // Added only when present so an ordinary index hashes as it did before.
        if (includedColumns is not null)
        {
            var included = new Relationship(PostgresRelationshipNames.IncludedColumns);

            foreach (var includedColumn in includedColumns)
            {
                included.Add(new Reference(includedColumn));
            }

            if (included.Entries.Count > 0)
            {
                element.Relationships.Add(included);
            }
        }

        // NULLS NOT DISTINCT (PostgreSQL 15+, issue #160) inverts how a unique index treats
        // NULLs. Stored only when true, matching the catalog's default of false, so an ordinary
        // index does not re-diff.
        if (nullsNotDistinct)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.NullsNotDistinct, true));
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
    public static Element CreateExtension(SqlName name, string? version = null, bool cascade = false)
    {
        var element = new Element(PostgresElementTypes.SqlExtension)
        {
            Name = name,
        };

        if (version is not null)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.Version, version));
        }

        // Scripted but never compared — see PostgresPropertyNames.Cascade. A model extracted
        // from a database can never set this, so hashing it would make the two sides disagree
        // forever.
        if (cascade)
        {
            element.Properties.Add(
                new Property(PostgresPropertyNames.Cascade, true, participatesInIdentity: false));
        }

        return element;
    }

    /// <summary>
    /// Builds a composite-type element (issue #122) — <c>CREATE TYPE name AS (field type, ...)</c>.
    ///
    /// The attributes are carried in a <c>Columns</c> relationship, the same shape a table's
    /// columns use, so the existing column-type machinery (type specifiers, Length /
    /// Precision / Scale properties) applies unchanged to both model builders and to scripting.
    /// Attribute order is significant — it is the field order of the type's row values — so
    /// the declared order is preserved rather than sorted.
    /// </summary>
    public static Element CreateCompositeType(SqlName name, string schema,
        IEnumerable<Element> attributes)
    {
        var columns = new Relationship(SqlRelationshipNames.Columns);

        foreach (var attribute in attributes)
        {
            columns.Add(attribute);
        }

        return new Element(PostgresElementTypes.SqlCompositeType)
        {
            Name = name,
            Relationships =
            {
                new Relationship(PostgresRelationshipNames.Schema)
                {
                    new Reference(schema) { ExternalSource = "BuiltIns" },
                },
                columns,
            },
        };
    }

    /// <summary>
    /// Reads a composite type's attributes, in declaration order. Centralized here so the
    /// model builders, the diff and the script generator agree on where they live.
    /// </summary>
    public static IReadOnlyList<Element> GetCompositeTypeAttributes(Element element)
        => element.GetRelationship(SqlRelationshipNames.Columns)
            ?.Entries.OfType<Element>().ToList() ?? [];

    /// <summary>
    /// Builds a range-type element (issue #122) — <c>CREATE TYPE name AS RANGE (SUBTYPE = ...)</c>.
    ///
    /// <paramref name="subtype"/> is the canonical subtype name and is what gives the type its
    /// identity. The operator class and collation are stored only when given: PostgreSQL
    /// resolves an omitted opclass to the subtype's default and the catalog then always
    /// reports one, so storing a default would stop a declared range from hash-matching an
    /// extracted one — the same omit-when-default convention used elsewhere.
    /// </summary>
    public static Element CreateRangeType(SqlName name, string schema, string subtype,
        string? subtypeOperatorClass, string? collation)
    {
        var element = new Element(PostgresElementTypes.SqlRangeType)
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

        element.Properties.Add(new Property(PostgresPropertyNames.Subtype, subtype));

        if (subtypeOperatorClass is not null)
        {
            element.Properties.Add(
                new Property(PostgresPropertyNames.SubtypeOperatorClass, subtypeOperatorClass));
        }

        if (collation is not null)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.Collation, collation));
        }

        return element;
    }

    /// <summary>
    /// Builds a collation element (issue #159) — <c>CREATE COLLATION name (...)</c>.
    ///
    /// The facets are the ones pg_collation stores, not the items the source wrote: PostgreSQL
    /// resolves <c>LOCALE</c> and the <c>FROM</c> form into <paramref name="lcCollate"/> /
    /// <paramref name="lcCtype"/> for the libc provider and into <paramref name="locale"/> for
    /// icu, keeping no record of the spelling. Storing what was written instead would make one
    /// of the equivalent spellings re-diff on every deploy (measured — see
    /// <c>CreateCollationStatement</c>).
    /// </summary>
    public static Element CreateCollation(SqlName name, string schema, string provider,
        string? locale, string? lcCollate, string? lcCtype, bool isDeterministic)
    {
        var element = new Element(PostgresElementTypes.SqlCollation)
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

        element.Properties.Add(new Property(PostgresPropertyNames.Provider, provider));

        if (locale is not null)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.Locale, locale));
        }

        if (lcCollate is not null)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.LcCollate, lcCollate));
        }

        if (lcCtype is not null)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.LcCtype, lcCtype));
        }

        // Deterministic is the default, so only a non-deterministic collation records the
        // property — the same omit-when-default convention used elsewhere.
        if (!isDeterministic)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.IsDeterministic, false));
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
    ///
    /// <paramref name="modifiers"/> carries the optional declaration clauses (issue #214).
    /// Each is stored only when it was declared, so an ordinary trigger carries none of them
    /// and a model built before they existed still hash-matches.
    /// </summary>
    public static Element CreateTrigger(
        string schema,
        string triggerName,
        SqlName table,
        string timing,
        string events,
        string level,
        string triggerFunction,
        string functionArguments,
        TriggerModifiers modifiers = default)
    {
        var element = new Element(PostgresElementTypes.SqlTrigger)
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

        if (modifiers.WhenCondition is { } whenCondition)
        {
            AddExpressionProperties(
                element,
                PostgresPropertyNames.WhenCondition,
                PostgresPropertyNames.NormalizedWhenCondition,
                whenCondition);
        }

        // The column list keeps its declared order, so it is stored as written rather than
        // sorted: measured, PostgreSQL renders UPDATE OF back in the order it was given.
        if (!string.IsNullOrEmpty(modifiers.UpdateOfColumns))
        {
            element.Properties.Add(
                new Property(PostgresPropertyNames.UpdateOfColumns, modifiers.UpdateOfColumns));
        }

        if (modifiers.OldTransitionTable is { } oldTransitionTable)
        {
            element.Properties.Add(
                new Property(PostgresPropertyNames.OldTransitionTable, oldTransitionTable));
        }

        if (modifiers.NewTransitionTable is { } newTransitionTable)
        {
            element.Properties.Add(
                new Property(PostgresPropertyNames.NewTransitionTable, newTransitionTable));
        }

        // Each stored only when true, matching pg_trigger's plain booleans, so an ordinary
        // trigger carries no property at all.
        if (modifiers.IsConstraintTrigger)
        {
            element.Properties.Add(
                new Property(PostgresPropertyNames.IsConstraintTrigger, true));
        }

        if (modifiers.IsDeferrable)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.IsDeferrable, true));
        }

        if (modifiers.IsInitiallyDeferred)
        {
            element.Properties.Add(
                new Property(PostgresPropertyNames.IsInitiallyDeferred, true));
        }

        return element;
    }

    /// <summary>
    /// The optional clauses of a <c>CREATE TRIGGER</c> declaration (issue #214). Every member
    /// defaults to absent, which is what an ordinary trigger declares.
    /// </summary>
    /// <param name="WhenCondition">The raw <c>WHEN (...)</c> predicate, or null.</param>
    /// <param name="UpdateOfColumns">
    /// The comma-joined <c>UPDATE OF</c> column list in declared order, or empty.
    /// </param>
    /// <param name="OldTransitionTable">The REFERENCING OLD TABLE name, or null.</param>
    /// <param name="NewTransitionTable">The REFERENCING NEW TABLE name, or null.</param>
    /// <param name="IsConstraintTrigger">Whether this is a CREATE CONSTRAINT TRIGGER.</param>
    /// <param name="IsDeferrable">Whether a constraint trigger may be deferred.</param>
    /// <param name="IsInitiallyDeferred">Whether a constraint trigger defers by default.</param>
    public readonly record struct TriggerModifiers(
        string? WhenCondition = null,
        string? UpdateOfColumns = null,
        string? OldTransitionTable = null,
        string? NewTransitionTable = null,
        bool IsConstraintTrigger = false,
        bool IsDeferrable = false,
        bool IsInitiallyDeferred = false);

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
        string? definition,
        string? checkOption = null,
        bool? securityInvoker = null,
        bool? securityBarrier = null)
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

        // Issue #208: the facets that decide how the view executes. Unlike the query these do
        // round-trip -- pg_class.reloptions reports each one back exactly as declared -- so
        // they take part in the element's identity, and a view whose CHECK OPTION or security
        // setting changed is detected as changed.
        if (checkOption is not null)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.CheckOption, checkOption));
        }

        // Recorded whenever written, including when written as false. Measured on 18,
        // security_invoker=false is stored in reloptions rather than dropped, so an explicit
        // default and an absent one are different states in the catalog and must stay
        // different here -- the opposite of the omit-when-default rule most facets follow.
        if (securityInvoker is not null)
        {
            element.Properties.Add(
                new Property(PostgresPropertyNames.SecurityInvoker, securityInvoker.Value));
        }

        if (securityBarrier is not null)
        {
            element.Properties.Add(
                new Property(PostgresPropertyNames.SecurityBarrier, securityBarrier.Value));
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
        ReferentialAction onUpdate,
        bool isDeferrable = false,
        bool isInitiallyDeferred = false,
        string schema = "public",
        bool isMatchFull = false)
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
                // Carried for the same reason as on a primary key: the defining-table reference
                // is a bare name, so the schema is what distinguishes two same-named foreign
                // keys on same-named tables in different schemas (issue #200).
                new Relationship(PostgresRelationshipNames.Schema)
                {
                    new Reference(schema) { ExternalSource = "BuiltIns" }
                },
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

        // NOT DEFERRABLE INITIALLY IMMEDIATE is the Postgres default, so each flag is stored
        // only when true — matching what pg_constraint reports (issue #159).
        if (isDeferrable)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.IsDeferrable, true));
        }

        if (isInitiallyDeferred)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.IsInitiallyDeferred, true));
        }

        // MATCH SIMPLE is the Postgres default and is what an omitted clause means, so only
        // MATCH FULL is stored (issue #205) -- storing the default would make `REFERENCES p (x)`
        // and `REFERENCES p (x) MATCH SIMPLE` hash differently despite being the same
        // constraint. MATCH PARTIAL never reaches here: the provider rejects it at build time,
        // because PostgreSQL does not implement it.
        if (isMatchFull)
        {
            element.Properties.Add(new Property(
                PostgresPropertyNames.MatchType, ForeignKeyMatchType.Full.ToString()));
        }

        return element;
    }
}

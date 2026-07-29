using Squill.Core;
using Squill.MariaDbParser;
using Squill.MariaDbParser.Syntax;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Owns the shape of every MariaDB model element. Both the parser-based and the
/// database-extraction model builders construct elements through this factory, so the two
/// representations agree by construction rather than by hand — a prerequisite for
/// comparing a parsed model against an extracted one.
///
/// Unlike the Postgres factory, MariaDB elements carry no schema relationship (a MariaDB
/// "schema" is the database itself, so objects are not schema-scoped within it) and model
/// auto-increment rather than Postgres identity.
/// </summary>
public static class MariaDbModelFactory
{
    public static Element CreateTable(SqlName name)
        => new(MariaDbElementTypes.SqlTable)
        {
            Name = name,
        };

    /// <summary>
    /// Describes one index key: its canonical column reference plus optional sort direction,
    /// prefix length and — for a functional key — the expression in place of a column.
    /// A null facet means "unspecified" and is omitted from the model.
    /// </summary>
    /// <remarks>
    /// An expression key (<c>CREATE INDEX ix ON t ((a + b))</c>) sets
    /// <paramref name="KeyExpression"/> instead of naming a column; <paramref name="Column"/>
    /// is then the index's own name, used only to give the spec a stable identity — the same
    /// shape the Postgres provider uses for its expression keys (issue #160).
    /// </remarks>
    public readonly record struct IndexedColumn(
        SqlName Column,
        bool? IsAscending = null,
        int? PrefixLength = null,
        string? KeyExpression = null);

    public static Element CreateIndexedColumnSpecification(IndexedColumn column)
    {
        var element = new Element(MariaDbElementTypes.SqlIndexedColumnSpecification);

        // An expression key is text rather than a reference to a column, so it replaces the
        // Column relationship instead of joining it (issue #161). Split raw-versus-canonical
        // exactly as a CHECK predicate is (issue #156): measured on mysql:latest, a key
        // declared `(a + b)` is stored as `` (`a` + `b`) ``, so only the canonical form can
        // compare — the raw text would re-diff on every deploy.
        if (column.KeyExpression is { } keyExpression)
        {
            AddExpressionProperties(
                element,
                MariaDbPropertyNames.KeyExpression,
                MariaDbPropertyNames.NormalizedKeyExpression,
                keyExpression);
        }
        else
        {
            element.Relationships.Add(new Relationship(MariaDbRelationshipNames.Column)
            {
                new Reference(column.Column)
            });
        }

        if (column.IsAscending is bool isAscending)
        {
            element.Properties.Add(new Property(MariaDbPropertyNames.IsAscending, isAscending));
        }

        // The declared prefix length — the 20 in Brand(20) (issue #161). Recorded only when one
        // is written, matching the catalog: information_schema.STATISTICS reports SUB_PART NULL
        // for a whole-column key, so storing a value unconditionally would make every ordinary
        // index re-diff on every deploy.
        if (column.PrefixLength is int prefixLength)
        {
            element.Properties.Add(new Property(MariaDbPropertyNames.PrefixLength, prefixLength));
        }

        return element;
    }

    public static Element CreatePrimaryKey(SqlName name, SqlName definingTable, IEnumerable<IndexedColumn> columns)
    {
        var columnSpecs = new Relationship(MariaDbRelationshipNames.ColumnSpecifications);

        foreach (var column in columns)
        {
            columnSpecs.Add(CreateIndexedColumnSpecification(column));
        }

        return new Element(MariaDbElementTypes.SqlPrimaryKeyConstraint)
        {
            Name = name,
            Relationships =
            {
                columnSpecs,
                new Relationship(MariaDbRelationshipNames.DefiningTable)
                {
                    new Reference(definingTable)
                }
            }
        };
    }

    /// <summary>
    /// A CHECK constraint on a table (issue #120). It has no column set of its own — the
    /// predicate may reference any columns of the table — so the element carries the
    /// predicate text and its defining table.
    ///
    /// The raw predicate cannot take part in comparison: both engines rewrite it when they
    /// store it (backtick-quoting identifiers, lower-casing keywords, and in MySQL wrapping the
    /// whole expression in parentheses), so a declared expression could never hash-match what
    /// information_schema.CHECK_CONSTRAINTS reports. Its canonical form does instead (issue
    /// #156), so redefining the predicate under the same constraint name is a change the deploy
    /// acts on rather than a silent no-op. The constraint's name and table still identify it,
    /// which is why an unnamed one is a build error: the two engines derive different names.
    /// </summary>
    public static Element CreateCheckConstraint(
        SqlName name, SqlName definingTable, string checkExpression)
    {
        var element = new Element(MariaDbElementTypes.SqlCheckConstraint)
        {
            Name = name,
            Relationships =
            {
                new Relationship(MariaDbRelationshipNames.DefiningTable)
                {
                    new Reference(definingTable)
                }
            }
        };

        AddExpressionProperties(
            element,
            MariaDbPropertyNames.CheckExpression,
            MariaDbPropertyNames.NormalizedCheckExpression,
            checkExpression);

        return element;
    }

    /// <summary>
    /// Records that a column is generated (computed) (issue #120). Called by both model
    /// builders so a parsed column and an extracted one carry the same properties in the
    /// same order (the Merkle hash is order-sensitive).
    ///
    /// As with a CHECK predicate the raw expression is carried for scripting and its canonical
    /// form is what participates in comparison (issue #156), since both engines rewrite the text
    /// they store. Also participating is whether the column is STORED or VIRTUAL, a real
    /// structural difference.
    /// </summary>
    public static void AddGeneratedColumnProperties(Element column, string? generationExpression,
        bool isStored)
    {
        column.Properties.Add(new Property(MariaDbPropertyNames.IsStored, isStored));

        if (generationExpression is not null)
        {
            AddExpressionProperties(
                column,
                MariaDbPropertyNames.GeneratedExpression,
                MariaDbPropertyNames.NormalizedGeneratedExpression,
                generationExpression);
        }
    }

    /// <summary>
    /// Records an expression as the pair of properties comparison and scripting each need: the
    /// raw text exactly as given, and — when one can be derived — its canonical form (issue #156).
    ///
    /// Only the canonical form takes part in identity. When the expression cannot be normalized,
    /// no canonical property is added and the raw one stays out of identity too, so the element
    /// falls back to the pre-#156 behaviour: a redefinition is missed, rather than an unchanged
    /// expression looking changed and redeploying forever.
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

    public static Element CreateIndex(SqlName name,
        SqlName indexedObject,
        bool isUnique,
        string? indexMethod,
        IEnumerable<IndexedColumn> columns,
        string? indexKind = null)
    {
        var columnSpecs = new Relationship(MariaDbRelationshipNames.ColumnSpecifications);

        foreach (var column in columns)
        {
            columnSpecs.Add(CreateIndexedColumnSpecification(column));
        }

        var element = new Element(MariaDbElementTypes.SqlIndex)
        {
            Name = name,
            Relationships =
            {
                columnSpecs,
                new Relationship(MariaDbRelationshipNames.IndexedObject)
                {
                    new Reference(indexedObject)
                }
            }
        };

        element.Properties.Add(new Property(MariaDbPropertyNames.IsUnique, isUnique));

        if (indexMethod is not null)
        {
            element.Properties.Add(new Property(MariaDbPropertyNames.IndexMethod, indexMethod));
        }

        // Emitted after the method so both builders agree on property order (the hash is
        // order-sensitive). A FULLTEXT/SPATIAL index carries a kind and no method; an ordinary
        // one the reverse, so the two are never both present in practice.
        if (indexKind is not null)
        {
            element.Properties.Add(new Property(MariaDbPropertyNames.IndexKind, indexKind));
        }

        return element;
    }

    /// <summary>
    /// Builds a foreign key constraint element. Referencing and referenced columns are
    /// ordered, canonical (table-qualified) references so a composite key's column pairing
    /// survives. RESTRICT is MariaDB's default referential action and is stored as an absent
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
        var columnRelationship = new Relationship(MariaDbRelationshipNames.ForeignKeyColumns);

        foreach (var column in columns)
        {
            columnRelationship.Add(new Reference(column));
        }

        var foreignColumnRelationship = new Relationship(MariaDbRelationshipNames.ForeignColumns);

        foreach (var foreignColumn in foreignColumns)
        {
            foreignColumnRelationship.Add(new Reference(foreignColumn));
        }

        var element = new Element(MariaDbElementTypes.SqlForeignKeyConstraint)
        {
            Name = name,
            Relationships =
            {
                columnRelationship,
                new Relationship(MariaDbRelationshipNames.DefiningTable)
                {
                    new Reference(definingTable)
                },
                new Relationship(MariaDbRelationshipNames.ForeignTable)
                {
                    new Reference(foreignTable)
                },
                foreignColumnRelationship,
            }
        };

        // MariaDB's default ON DELETE / ON UPDATE action is RESTRICT; only a non-default
        // action is stored, so parsed and extracted models hash-match when none is written.
        if (onDelete != ReferentialAction.Restrict)
        {
            element.Properties.Add(new Property(MariaDbPropertyNames.DeleteAction, onDelete.ToString()));
        }

        if (onUpdate != ReferentialAction.Restrict)
        {
            element.Properties.Add(new Property(MariaDbPropertyNames.UpdateAction, onUpdate.ToString()));
        }

        return element;
    }

    /// <summary>The data-access clause both engines report when none is written.</summary>
    public const string DefaultSqlDataAccess = "CONTAINS SQL";

    /// <summary>
    /// A procedure parameter as it appears in the routine's declaration.
    /// </summary>
    /// <param name="Mode">IN, OUT or INOUT, spelled as the engines write it.</param>
    /// <param name="Name">The parameter name. Both engines always name a parameter.</param>
    /// <param name="Type">
    /// The engine-normalized type (e.g. <c>varchar(50)</c>, <c>int</c>), with any integer
    /// display width discarded — see <see cref="MariaDbTypeNormalizer"/>.
    /// </param>
    public readonly record struct ProcedureParameter(
        string Mode,
        string Name,
        string Type);

    /// <summary>
    /// Builds a stored procedure element.
    ///
    /// Unlike PostgreSQL, neither MariaDB nor MySQL allows routine overloading — a name
    /// identifies at most one procedure in a database — so the element's name is the bare
    /// routine name with no argument signature folded in.
    ///
    /// Only non-default facets are stored, so a procedure written without any
    /// characteristic clause produces the same element shape as one extracted from a
    /// database, which reports the defaults explicitly. Both engines default to
    /// NOT DETERMINISTIC, CONTAINS SQL and SQL SECURITY DEFINER.
    /// </summary>
    public static Element CreateProcedure(
        SqlName name,
        string body,
        IEnumerable<ProcedureParameter> parameters,
        bool isDeterministic = false,
        string? sqlDataAccess = null,
        bool isSecurityInvoker = false)
    {
        var element = new Element(MariaDbElementTypes.SqlProcedure)
        {
            Name = name,
            Properties =
            {
                new Property(MariaDbPropertyNames.Arguments, RenderParameters(parameters)),
                new Property(MariaDbPropertyNames.Body, body),
            },
        };

        if (isDeterministic)
        {
            element.Properties.Add(new Property(MariaDbPropertyNames.IsDeterministic, true));
        }

        if (sqlDataAccess is not null
            && !string.Equals(sqlDataAccess, DefaultSqlDataAccess, StringComparison.Ordinal))
        {
            element.Properties.Add(new Property(MariaDbPropertyNames.SqlDataAccess, sqlDataAccess));
        }

        if (isSecurityInvoker)
        {
            element.Properties.Add(new Property(MariaDbPropertyNames.IsSecurityInvoker, true));
        }

        return element;
    }

    /// <summary>
    /// Builds a stored function element (issue #74).
    ///
    /// A function mirrors a procedure — same body, parameter, and characteristic handling —
    /// but declares a return type and takes only IN parameters. As with a procedure, neither
    /// engine allows overloading, so the element's name is the bare function name. Only
    /// non-default characteristics are stored so a parsed function has the same shape as one
    /// extracted from a database, which reports the defaults (NOT DETERMINISTIC, CONTAINS SQL,
    /// SQL SECURITY DEFINER) explicitly.
    /// </summary>
    public static Element CreateFunction(
        SqlName name,
        string returnType,
        string body,
        IEnumerable<ProcedureParameter> parameters,
        bool isDeterministic = false,
        string? sqlDataAccess = null,
        bool isSecurityInvoker = false)
    {
        var element = new Element(MariaDbElementTypes.SqlFunction)
        {
            Name = name,
            Properties =
            {
                new Property(MariaDbPropertyNames.Arguments, RenderParameters(parameters)),
                new Property(MariaDbPropertyNames.ReturnType, returnType),
                new Property(MariaDbPropertyNames.Body, body),
            },
        };

        if (isDeterministic)
        {
            element.Properties.Add(new Property(MariaDbPropertyNames.IsDeterministic, true));
        }

        if (sqlDataAccess is not null
            && !string.Equals(sqlDataAccess, DefaultSqlDataAccess, StringComparison.Ordinal))
        {
            element.Properties.Add(new Property(MariaDbPropertyNames.SqlDataAccess, sqlDataAccess));
        }

        if (isSecurityInvoker)
        {
            element.Properties.Add(new Property(MariaDbPropertyNames.IsSecurityInvoker, true));
        }

        return element;
    }

    /// <summary>
    /// Builds a scheduled event element (issue #122).
    ///
    /// An event's identity is its name plus the schedule it runs on and the body it runs. It
    /// is not bound to a table — unlike a trigger it is driven by the clock — so its element
    /// name is simply its own name and it carries no relationships.
    ///
    /// The schedule facets mirror what <c>information_schema.EVENTS</c> reports, and follow
    /// the omit-when-default convention used throughout the model: the catalog always reports
    /// every column with defaults filled in, so a facet equal to its default (ENABLED status,
    /// NOT PRESERVE, no comment) is never stored. That is what lets a model parsed from
    /// source hash-match one extracted from a live database.
    ///
    /// <paramref name="eventType"/> selects which schedule facets apply: a <c>ONE TIME</c>
    /// event carries only <paramref name="executeAt"/>, while a <c>RECURRING</c> one carries
    /// the interval and its start (and optionally its end).
    /// </summary>
    public static Element CreateEvent(
        string eventName,
        string eventType,
        string body,
        string? executeAt = null,
        string? intervalValue = null,
        string? intervalField = null,
        string? starts = null,
        string? ends = null,
        string status = EnabledStatus,
        bool preserveOnCompletion = false,
        string? comment = null)
    {
        var element = new Element(MariaDbElementTypes.SqlEvent)
        {
            Name = SqlName.Object(eventName),
            Properties =
            {
                new Property(MariaDbPropertyNames.EventType, eventType),
                new Property(MariaDbPropertyNames.Body, body),
            },
        };

        AddIfNotNull(element, MariaDbPropertyNames.ExecuteAt, executeAt);
        AddIfNotNull(element, MariaDbPropertyNames.IntervalValue, intervalValue);
        AddIfNotNull(element, MariaDbPropertyNames.IntervalField, intervalField);
        AddIfNotNull(element, MariaDbPropertyNames.Starts, starts);
        AddIfNotNull(element, MariaDbPropertyNames.Ends, ends);

        // Omit-when-default: ENABLED, NOT PRESERVE and an empty comment are what the catalog
        // reports for an event that declared none of them, so storing them would make a
        // declaration that omits them differ from the deployed object.
        if (status != EnabledStatus)
        {
            element.Properties.Add(new Property(MariaDbPropertyNames.Status, status));
        }

        if (preserveOnCompletion)
        {
            element.Properties.Add(
                new Property(MariaDbPropertyNames.PreserveOnCompletion, true));
        }

        AddIfNotNull(element, MariaDbPropertyNames.Comment, comment);

        return element;
    }

    /// <summary>The event status both engines report when none was declared.</summary>
    public const string EnabledStatus = "ENABLED";

    private static void AddIfNotNull(Element element, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            element.Properties.Add(new Property(name, value));
        }
    }

    /// <summary>
    /// Builds a trigger element (issue #100).
    ///
    /// A trigger's identity is its name, the table it fires on, and its behavior: the
    /// <paramref name="timing"/> (BEFORE/AFTER), the <paramref name="event"/>
    /// (INSERT/UPDATE/DELETE) and the <paramref name="body"/> it runs. All of these round-trip
    /// faithfully through <c>information_schema.TRIGGERS</c> (ACTION_TIMING, EVENT_MANIPULATION,
    /// ACTION_STATEMENT), so a parsed model hash-matches one extracted from a live database.
    ///
    /// The name is folded as <c>table.trigger</c> so same-named triggers on different tables
    /// stay distinct in the model and a trigger sorts alongside its table; the bare trigger
    /// name is recovered from the trailing segment when scripting. The table it fires on is
    /// carried as a relationship so the trigger follows its table on deploy.
    /// </summary>
    public static Element CreateTrigger(
        SqlName table,
        string triggerName,
        string timing,
        string @event,
        string body)
    {
        return new Element(MariaDbElementTypes.SqlTrigger)
        {
            Name = table.Sibling($"{table.UnqualifiedName}.{triggerName}"),
            Relationships =
            {
                new Relationship(MariaDbRelationshipNames.TriggerTable)
                {
                    new Reference(table)
                },
            },
            Properties =
            {
                new Property(MariaDbPropertyNames.RoutineName, triggerName),
                new Property(MariaDbPropertyNames.Timing, timing),
                new Property(MariaDbPropertyNames.Event, @event),
                new Property(MariaDbPropertyNames.Body, body),
            },
        };
    }

    /// <summary>
    /// Builds a view element (issue #42).
    ///
    /// A view's identity is its name and its ordered column list — the facets both engines
    /// report back faithfully through <c>information_schema.COLUMNS</c>. The query itself is
    /// stored as <see cref="MariaDbPropertyNames.Definition"/> for scripting, but opts out
    /// of the element's hash: MariaDB and MySQL each rewrite a view's query when they store
    /// it, and not even in the same way as each other (MySQL parenthesizes a WHERE clause
    /// where MariaDB does not, and both embed the database name), so a declared query could
    /// never match an extracted one and would force a recreate on every deploy.
    ///
    /// The trade-off this buys is deliberate: a changed query that leaves the column list
    /// untouched is not detected as a change. Adding, removing, renaming or reordering a
    /// column is.
    /// </summary>
    /// <param name="definition">
    /// The declared query, for scripting. Null when the element comes from a live database,
    /// which holds only its own rewritten copy.
    /// </param>
    public static Element CreateView(
        SqlName name,
        IEnumerable<string> columnNames,
        string? definition)
    {
        var columns = new Relationship(MariaDbRelationshipNames.Columns);

        foreach (var columnName in columnNames)
        {
            columns.Add(new Element(MariaDbElementTypes.SqlViewColumn)
            {
                Name = name.Child(columnName),
            });
        }

        var element = new Element(MariaDbElementTypes.SqlView)
        {
            Name = name,
            Relationships = { columns },
        };

        if (definition is not null)
        {
            element.Properties.Add(new Property(
                MariaDbPropertyNames.Definition, definition, participatesInIdentity: false));
        }

        return element;
    }

    /// <summary>
    /// Renders a procedure's parameter list as it is written in a CREATE PROCEDURE: mode
    /// first (IN is always written), then name, then type. Both model builders render
    /// through this, so a parsed parameter list compares equal to an extracted one.
    /// </summary>
    private static string RenderParameters(IEnumerable<ProcedureParameter> parameters)
        => string.Join(", ", parameters.Select(i => $"{i.Mode} {i.Name} {i.Type}"));
}

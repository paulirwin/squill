using Squill.Core;
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
    /// Describes an indexed column: its canonical reference plus optional sort direction.
    /// A null direction means "unspecified" and is omitted from the model.
    /// </summary>
    public readonly record struct IndexedColumn(
        SqlName Column,
        bool? IsAscending = null);

    public static Element CreateIndexedColumnSpecification(IndexedColumn column)
    {
        var element = new Element(MariaDbElementTypes.SqlIndexedColumnSpecification)
        {
            Relationships =
            {
                new Relationship(MariaDbRelationshipNames.Column)
                {
                    new Reference(column.Column)
                }
            }
        };

        if (column.IsAscending is bool isAscending)
        {
            element.Properties.Add(new Property(MariaDbPropertyNames.IsAscending, isAscending));
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
    /// The predicate does not take part in comparison: both engines rewrite it when they
    /// store it (backtick-quoting identifiers, and in MySQL wrapping the whole expression in
    /// parentheses), so a declared expression could never hash-match what
    /// information_schema.CHECK_CONSTRAINTS reports. A CHECK constraint's modeled identity is
    /// its name and table instead, which is why an unnamed one is a build error: the two
    /// engines derive different names for it.
    /// </summary>
    public static Element CreateCheckConstraint(
        SqlName name, SqlName definingTable, string checkExpression)
        => new(MariaDbElementTypes.SqlCheckConstraint)
        {
            Name = name,
            Properties =
            {
                new Property(MariaDbPropertyNames.CheckExpression, checkExpression,
                    participatesInIdentity: false),
            },
            Relationships =
            {
                new Relationship(MariaDbRelationshipNames.DefiningTable)
                {
                    new Reference(definingTable)
                }
            }
        };

    /// <summary>
    /// Records that a column is generated (computed) (issue #120). Called by both model
    /// builders so a parsed column and an extracted one carry the same properties in the
    /// same order (the Merkle hash is order-sensitive).
    ///
    /// As with a CHECK predicate the expression is carried for scripting only and does not
    /// participate in comparison — both engines rewrite it. What does participate is whether
    /// the column is STORED or VIRTUAL, a real structural difference.
    /// </summary>
    public static void AddGeneratedColumnProperties(Element column, string? generationExpression,
        bool isStored)
    {
        column.Properties.Add(new Property(MariaDbPropertyNames.IsStored, isStored));

        if (generationExpression is not null)
        {
            column.Properties.Add(
                new Property(MariaDbPropertyNames.GeneratedExpression, generationExpression,
                    participatesInIdentity: false));
        }
    }

    public static Element CreateIndex(SqlName name,
        SqlName indexedObject,
        bool isUnique,
        string? indexMethod,
        IEnumerable<IndexedColumn> columns)
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

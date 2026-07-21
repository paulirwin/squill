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
    /// Renders a procedure's parameter list as it is written in a CREATE PROCEDURE: mode
    /// first (IN is always written), then name, then type. Both model builders render
    /// through this, so a parsed parameter list compares equal to an extracted one.
    /// </summary>
    private static string RenderParameters(IEnumerable<ProcedureParameter> parameters)
        => string.Join(", ", parameters.Select(i => $"{i.Mode} {i.Name} {i.Type}"));
}

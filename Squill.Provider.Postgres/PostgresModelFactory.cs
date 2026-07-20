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

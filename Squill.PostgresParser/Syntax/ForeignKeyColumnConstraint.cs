namespace Squill.PostgresParser.Syntax;

/// <summary>
/// An inline column-level foreign key: <c>REFERENCES table (column) [ON DELETE ...]
/// [ON UPDATE ...]</c> attached to a single column. The referencing column is the
/// column carrying the constraint, so it is not repeated here.
/// </summary>
public class ForeignKeyColumnConstraint : ColumnConstraint
{
    public ForeignKeyColumnConstraint(string text,
        QualifiedName referencedTable,
        Identifier? referencedColumn,
        ReferentialAction? onDelete,
        ReferentialAction? onUpdate)
        : base(text)
    {
        ReferencedTable = referencedTable;
        ReferencedColumn = referencedColumn;
        OnDelete = onDelete;
        OnUpdate = onUpdate;
    }

    public QualifiedName ReferencedTable { get; }

    /// <summary>The referenced column, or null when omitted (defaults to the referenced table's primary key).</summary>
    public Identifier? ReferencedColumn { get; }

    public ReferentialAction? OnDelete { get; }

    public ReferentialAction? OnUpdate { get; }

    /// <summary>
    /// The <c>MATCH</c> clause (issue #205), reachable inline as well as at table level.
    /// Defaults to <see cref="ForeignKeyMatchType.Simple"/>, matching PostgreSQL's own default.
    /// </summary>
    public ForeignKeyMatchType MatchType { get; set; } = ForeignKeyMatchType.Simple;
}

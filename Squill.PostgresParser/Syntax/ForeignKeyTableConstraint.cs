namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A table-level foreign key: <c>FOREIGN KEY (col, ...) REFERENCES table (col, ...)
/// [ON DELETE ...] [ON UPDATE ...]</c>. Supports composite keys, so both the
/// referencing and referenced column lists are ordered collections.
/// </summary>
public class ForeignKeyTableConstraint : TableConstraint
{
    public ForeignKeyTableConstraint(IEnumerable<Identifier> columns,
        QualifiedName referencedTable,
        IEnumerable<Identifier> referencedColumns,
        ReferentialAction? onDelete,
        ReferentialAction? onUpdate)
    {
        Columns = columns.ToList();
        ReferencedTable = referencedTable;
        ReferencedColumns = referencedColumns.ToList();
        OnDelete = onDelete;
        OnUpdate = onUpdate;
    }

    public IReadOnlyList<Identifier> Columns { get; }

    public QualifiedName ReferencedTable { get; }

    /// <summary>The referenced columns; empty when omitted (defaults to the referenced table's primary key).</summary>
    public IReadOnlyList<Identifier> ReferencedColumns { get; }

    public ReferentialAction? OnDelete { get; }

    public ReferentialAction? OnUpdate { get; }

    /// <summary>
    /// The <c>MATCH</c> clause (issue #205). Defaults to <see cref="ForeignKeyMatchType.Simple"/>,
    /// which is both the PostgreSQL default and what an omitted clause means, so the explicit
    /// and omitted spellings of the default land on one value and cannot re-diff against each
    /// other.
    /// </summary>
    public ForeignKeyMatchType MatchType { get; set; } = ForeignKeyMatchType.Simple;
}

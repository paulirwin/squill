namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A table-level primary key: <c>PRIMARY KEY (col, ...)</c>. Used for composite keys
/// (and single-column keys written at the table level rather than inline).
/// </summary>
public class PrimaryKeyTableConstraint : TableConstraint, IIndexBackedTableConstraint
{
    public PrimaryKeyTableConstraint(IEnumerable<Identifier> columns)
    {
        Columns = columns.ToList();
    }

    public IReadOnlyList<Identifier> Columns { get; }

    /// <summary>
    /// The existing index this constraint is built from (<c>PRIMARY KEY USING INDEX ix</c>),
    /// or null for the ordinary parenthesized form. When set, <see cref="Columns"/> is empty —
    /// the key columns belong to the index, not the constraint. Carried but not modeled
    /// (issue #143): the model cannot express a constraint bound to one specific index.
    /// </summary>
    public Identifier? UsingIndex { get; set; }

    /// <inheritdoc />
    public IList<Identifier> IncludeColumns { get; } = new List<Identifier>();

    /// <inheritdoc />
    public IList<IndexWithOption> WithOptions { get; } = new List<IndexWithOption>();

    /// <inheritdoc />
    public Identifier? TableSpace { get; set; }
}

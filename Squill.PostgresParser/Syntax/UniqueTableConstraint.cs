namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A table-level uniqueness constraint: <c>UNIQUE (col, ...)</c>. Used for composite
/// unique keys (and single-column ones written at the table level rather than inline).
/// </summary>
public class UniqueTableConstraint : TableConstraint
{
    public UniqueTableConstraint(IEnumerable<Identifier> columns)
    {
        Columns = columns.ToList();
    }

    public IReadOnlyList<Identifier> Columns { get; }

    /// <summary>
    /// The existing index this constraint is built from (<c>UNIQUE USING INDEX ix</c>), or null
    /// for the ordinary parenthesized form. When set, <see cref="Columns"/> is empty — the
    /// columns belong to the index. Carried but not modeled (issue #143), mirroring
    /// <see cref="PrimaryKeyTableConstraint.UsingIndex"/>.
    /// </summary>
    public Identifier? UsingIndex { get; set; }
}

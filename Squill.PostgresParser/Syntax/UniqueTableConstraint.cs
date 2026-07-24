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
}

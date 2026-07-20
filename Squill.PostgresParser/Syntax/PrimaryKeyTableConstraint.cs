namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A table-level primary key: <c>PRIMARY KEY (col, ...)</c>. Used for composite keys
/// (and single-column keys written at the table level rather than inline).
/// </summary>
public class PrimaryKeyTableConstraint : TableConstraint
{
    public PrimaryKeyTableConstraint(IEnumerable<Identifier> columns)
    {
        Columns = columns.ToList();
    }

    public IReadOnlyList<Identifier> Columns { get; }
}

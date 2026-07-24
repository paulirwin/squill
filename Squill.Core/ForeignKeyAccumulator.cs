namespace Squill.Core;

/// <summary>
/// Accumulates the ordered column pairs of one foreign key across the rows of a catalog query,
/// which report one (local column, referenced column) pair per row. The referenced table and the
/// referential actions are fixed on the first row seen for a constraint; each subsequent row for
/// the same constraint appends another column pair.
///
/// Both providers extract foreign keys the same way, differing only in the concrete
/// <see cref="SqlNameBase{TSelf}"/> (Postgres quotes with double quotes, MariaDB with backticks)
/// and in each parser's own <c>ReferentialAction</c> enum, so this is generic over both the name
/// type and the action type.
/// </summary>
public sealed class ForeignKeyAccumulator<TName, TAction>
    where TName : SqlNameBase<TName>
{
    public ForeignKeyAccumulator(
        TName referencedTable,
        TAction onDelete,
        TAction onUpdate)
    {
        ReferencedTable = referencedTable;
        OnDelete = onDelete;
        OnUpdate = onUpdate;
    }

    public TName ReferencedTable { get; }
    public TAction OnDelete { get; }
    public TAction OnUpdate { get; }
    public List<TName> Columns { get; } = new();
    public List<TName> ReferencedColumns { get; } = new();
}

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
        TAction onUpdate,
        bool isDeferrable = false,
        bool isInitiallyDeferred = false,
        bool isMatchFull = false)
    {
        ReferencedTable = referencedTable;
        OnDelete = onDelete;
        OnUpdate = onUpdate;
        IsDeferrable = isDeferrable;
        IsInitiallyDeferred = isInitiallyDeferred;
        IsMatchFull = isMatchFull;
    }

    public TName ReferencedTable { get; }
    public TAction OnDelete { get; }
    public TAction OnUpdate { get; }

    /// <summary>
    /// Whether the constraint is DEFERRABLE, and whether it is INITIALLY DEFERRED. Both are
    /// always false on engines with no deferrable constraints (MariaDB/MySQL), which is also
    /// the PostgreSQL default.
    /// </summary>
    public bool IsDeferrable { get; }
    public bool IsInitiallyDeferred { get; }

    /// <summary>
    /// Whether the foreign key is <c>MATCH FULL</c>, so a composite key must be either entirely
    /// NULL or entirely non-NULL. False means <c>MATCH SIMPLE</c>, which is the default on both
    /// engines and what an omitted clause means; <c>MATCH PARTIAL</c> never reaches here,
    /// because PostgreSQL does not implement it (issue #205).
    /// </summary>
    public bool IsMatchFull { get; }

    public List<TName> Columns { get; } = new();
    public List<TName> ReferencedColumns { get; } = new();
}

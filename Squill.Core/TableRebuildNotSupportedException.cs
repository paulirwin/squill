namespace Squill.Core;

/// <summary>
/// Thrown during schema comparison when a table change would require a rebuild, but the
/// rebuild cannot be performed safely given the current schema — for example when
/// another table has a foreign key referencing the table being rebuilt, which the
/// rebuild's rename-and-drop strategy cannot yet reconcile. Distinct from
/// <see cref="TableRebuildNotAllowedException"/>, which is a policy choice; this is a
/// capability gap.
/// </summary>
public class TableRebuildNotSupportedException : Exception
{
    public TableRebuildNotSupportedException(string tableName, string reason)
        : base($"Deploying changes to table '{tableName}' requires rebuilding the table, "
               + $"which is not supported here ({reason}).")
    {
        TableName = tableName;
        Reason = reason;
    }

    public string TableName { get; }

    public string Reason { get; }
}

namespace Squill.Core;

/// <summary>
/// Thrown during schema comparison when a change to a table can only be deployed by
/// rebuilding the table, but table rebuilds have been disallowed (the "allow table
/// rebuild" option is off). Mirrors SSDT's behavior of blocking data-motion operations
/// unless they are explicitly permitted, so a costly rebuild of a large table is never
/// performed unintentionally.
/// </summary>
public class TableRebuildNotAllowedException : Exception
{
    public TableRebuildNotAllowedException(string tableName, string reason)
        : base($"Deploying changes to table '{tableName}' requires rebuilding the table "
               + $"({reason}), but table rebuilds are disallowed. Re-run with table rebuilds "
               + "allowed to permit this change.")
    {
        TableName = tableName;
        Reason = reason;
    }

    /// <summary>The name of the table whose change required a disallowed rebuild.</summary>
    public string TableName { get; }

    /// <summary>Why the change required a rebuild rather than an in-place ALTER.</summary>
    public string Reason { get; }
}

namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A single entry in a view's select list, reduced to what naming the view's columns needs.
///
/// A view column takes its name from an explicit alias (<c>SELECT id AS the_id</c>), or
/// failing that from the column being selected (<c>SELECT id</c>). An entry that is neither —
/// an unaliased expression such as <c>SELECT qty * 2</c> — has no name Squill can derive, and
/// <see cref="IsWildcard"/> marks a <c>*</c> that must be expanded against the source table.
/// </summary>
public class ViewSelectColumn
{
    private ViewSelectColumn(string? alias, string? columnName, bool isWildcard, string? qualifier)
    {
        Alias = alias;
        ColumnName = columnName;
        IsWildcard = isWildcard;
        Qualifier = qualifier;
    }

    /// <summary>An explicit <c>AS</c> alias, if one was written.</summary>
    public string? Alias { get; }

    /// <summary>The selected column's own name, when the entry is a plain column reference.</summary>
    public string? ColumnName { get; }

    /// <summary>Whether this entry is a <c>*</c> wildcard.</summary>
    public bool IsWildcard { get; }

    /// <summary>
    /// The table qualifier on a wildcard or column reference (the <c>t</c> in <c>t.*</c>), if
    /// one was written.
    /// </summary>
    public string? Qualifier { get; }

    /// <summary>
    /// The name this entry gives the view's column, or null when none can be derived — an
    /// unaliased expression, which the model builder reports as an error.
    /// </summary>
    public string? DerivedName => Alias ?? ColumnName;

    public static ViewSelectColumn Named(string columnName, string? qualifier = null)
        => new(alias: null, columnName, isWildcard: false, qualifier);

    public static ViewSelectColumn Aliased(string alias)
        => new(alias, columnName: null, isWildcard: false, qualifier: null);

    public static ViewSelectColumn Wildcard(string? qualifier = null)
        => new(alias: null, columnName: null, isWildcard: true, qualifier);

    /// <summary>An entry with no derivable name, e.g. an unaliased <c>qty * 2</c>.</summary>
    public static ViewSelectColumn Unnamed()
        => new(alias: null, columnName: null, isWildcard: false, qualifier: null);
}

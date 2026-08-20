namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>SET</c> or <c>RESET</c> configuration clause on a routine declaration (issue #213),
/// as in <c>SET search_path = pg_catalog, pg_temp</c>. Attaching that particular setting to
/// a <c>SECURITY DEFINER</c> routine is the hardening idiom the PostgreSQL documentation
/// recommends, so it is modeled rather than dropped.
///
/// The values are held as a list rather than one string because PostgreSQL stores a list
/// GUC's items separately. Measured on postgres:18.4: <c>SET search_path = pg_catalog,
/// pg_temp</c> and <c>SET search_path TO 'pg_catalog', 'pg_temp'</c> both store
/// <c>search_path=pg_catalog, pg_temp</c>, whereas passing the whole thing as one quoted
/// string stores <c>search_path="pg_catalog, pg_temp"</c> — a different, single-element
/// value. Keeping the items apart is what lets the clause be re-emitted so it round-trips.
/// </summary>
public class RoutineSetting : SyntaxNode
{
    public RoutineSetting(string name)
    {
        Name = name;
    }

    /// <summary>
    /// The configuration parameter's name as written. PostgreSQL folds this to the GUC's
    /// canonical spelling when it stores it (measured: <c>timezone</c> comes back as
    /// <c>TimeZone</c>), so the declared spelling is not necessarily what round-trips.
    /// </summary>
    public string Name { get; }

    /// <summary>The values assigned, one entry per list item. Empty for a RESET.</summary>
    public IList<string> Values { get; } = new List<string>();

    /// <summary>
    /// Whether the clause was written <c>RESET</c> rather than <c>SET</c>. A RESET on a
    /// routine declaration leaves <c>proconfig</c> null, which is indistinguishable from
    /// having written no clause at all, so it cannot be modeled.
    /// </summary>
    public bool IsReset { get; set; }

    /// <summary>Whether the clause was <c>RESET ALL</c> rather than naming one parameter.</summary>
    public bool IsAll { get; set; }

    /// <summary>
    /// Whether the value was written <c>FROM CURRENT</c>, which captures the creating
    /// session's value rather than a declared one (measured: <c>SET search_path FROM
    /// CURRENT</c> stored <c>search_path="$user", public</c>). That makes the stored value
    /// depend on who ran the deploy, so it cannot round-trip.
    /// </summary>
    public bool FromCurrent { get; set; }
}

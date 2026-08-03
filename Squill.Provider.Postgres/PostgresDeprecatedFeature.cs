namespace Squill.Provider.Postgres;

/// <summary>
/// A PostgreSQL construct every supported major still accepts, but which the documentation
/// advises against using (issue #190) — reported as SQ1006.
///
/// <para>
/// A different axis from <see cref="PostgresVersionedFeature"/>, which is why it is a separate
/// type. That one carries a minimum major and asks whether the declared target is old enough to
/// be a problem; here no version is implicated — the construct works on every supported major,
/// newest included — so there is no version to record and raising the target would resolve
/// nothing. The remedy is to change the source.
/// </para>
///
/// <para>
/// The bar for an entry is an explicit statement in PostgreSQL's own documentation, and it is a
/// high bar deliberately. The research behind issue #190 checked the obvious candidates and
/// <em>disproved</em> most of them: <c>money</c> carries a locale caveat but no deprecation,
/// <c>CREATE RULE</c> steers toward triggers without deprecating the rule system, <c>serial</c>
/// is presented as an alternative to identity columns rather than a superseded form,
/// <c>INHERITS</c> documents real caveats but no removal, and <c>character(n)</c> is advised
/// against only in the sense that something else is usually better. None of those are reported.
/// Warning on constructs that merely feel dated is how a diagnostic code earns a blanket
/// suppression.
/// </para>
/// </summary>
/// <param name="Description">
/// How the construct is named in the warning, spelled as it appears in source.
/// </param>
/// <param name="Remedy">
/// What to write instead, stated concretely. Unlike a version warning, this can never be resolved
/// by raising a number, so the message has to carry the alternative.
/// </param>
/// <param name="DocumentationUrl">
/// The official page establishing the advice, cited in the warning itself — the author's next
/// question after "do not use this" is always "says who?".
/// </param>
public readonly record struct PostgresDeprecatedFeature(
    string Description,
    string Remedy,
    string DocumentationUrl)
{
    /// <summary>
    /// The <c>time with time zone</c> type.
    ///
    /// "We do <em>not</em> recommend using the type <c>time with time zone</c> (though it is
    /// supported by PostgreSQL for legacy applications and for compliance with the SQL standard)."
    /// (https://www.postgresql.org/docs/current/datatype-datetime.html — italics in the original.)
    ///
    /// This is the one genuine PostgreSQL deprecation the issue #190 research found, and it is
    /// reported despite the "supported […] for compliance with the SQL standard" clause meaning it
    /// is unlikely to be removed: the documentation's objection is that the type is not useful
    /// rather than that it is going away. A time-of-day carries no date, and without one there is
    /// no way to resolve the UTC offset a time zone implies, so the stored offset cannot account
    /// for daylight saving.
    /// </summary>
    public static readonly PostgresDeprecatedFeature TimeWithTimeZone = new(
        "time with time zone",
        "use timestamp with time zone, which carries the date a time zone needs to resolve an "
            + "offset, or a plain time if no zone is meant",
        "https://www.postgresql.org/docs/current/datatype-datetime.html");
}

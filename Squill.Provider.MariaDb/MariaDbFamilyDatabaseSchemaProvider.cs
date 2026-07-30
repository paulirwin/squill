using Squill.Dacpac;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Base for both engines this provider serves — MariaDB and MySQL. Holds the capabilities that
/// are meaningful to <em>this family</em> and to no other engine, so they stay off the universal
/// <see cref="DatabaseSchemaProvider"/> where a Postgres provider would inherit questions that
/// have no answer for it (Postgres canonicalizes column defaults on entirely different rules —
/// it preserves the spelling it was given, and none of these tokens are part of its model).
///
/// Everything here is a capability the two engines answer <em>differently</em>. Where they agree
/// there is nothing to declare, so the members below are exactly the measured divergences
/// (issue #147) rather than a general description of either engine.
/// </summary>
public abstract class MariaDbFamilyDatabaseSchemaProvider : DatabaseSchemaProvider
{
    /// <summary>
    /// Both engines cap an identifier at 64 <em>characters</em> and reject a longer one with
    /// <c>ERROR 1059 (ER_TOO_LONG_IDENT)</c>. This is one of the few places the family agrees
    /// and still has to be stated, because the universal capability is abstract — and the
    /// answer is a genuine divergence from PostgreSQL, which counts 63 bytes instead.
    /// https://dev.mysql.com/doc/refman/8.4/en/identifier-length.html
    /// </summary>
    public sealed override int MaxIdentifierLength => 64;

    /// <summary>
    /// Counts characters, not UTF-16 code units: an identifier outside the BMP (an emoji, which
    /// <c>utf8mb4</c> accepts) is one character to the engine but two units to
    /// <see cref="string.Length"/>, so measuring units would reject a name the server takes.
    /// </summary>
    public sealed override int MeasureIdentifier(string identifier)
        => identifier.EnumerateRunes().Count();

    /// <summary>
    /// Which of the two engines this family serves is being targeted. Unlike every other
    /// capability here it describes no single behaviour — it exists because a construct's
    /// version boundary can differ between the engines (<c>VECTOR</c> is MySQL 9 and MariaDB 11)
    /// or be absent from one of them entirely (MySQL has no <c>UUID</c> type), and a per-feature
    /// boolean would mean adding a member to all five providers for every construct gated.
    ///
    /// <para>
    /// It is deliberately the only member of its kind. Reach for a named capability describing
    /// the behaviour whenever one can be written — this is for the version table in
    /// <see cref="MariaDbVersionedFeature"/>, which is keyed by engine by its nature, and not a
    /// licence to branch on the engine elsewhere.
    /// </para>
    /// </summary>
    public abstract bool IsMySql { get; }

    /// <summary>
    /// Whether <c>LOCALTIME</c> and <c>LOCALTIMESTAMP</c> are true synonyms for
    /// <c>CURRENT_TIMESTAMP</c> in a column <c>DEFAULT</c>, and so fold into its canonical
    /// token.
    ///
    /// Measured against <c>mysql:latest</c>, both spellings are stored and reported as
    /// <c>CURRENT_TIMESTAMP</c>. On <c>mariadb:latest</c> they are separate functions with
    /// their own stored forms — <c>DEFAULT LOCALTIME</c> becomes <c>curtime()</c>, a
    /// <em>time of day</em>, and <c>LOCALTIMESTAMP</c> becomes <c>localtimestamp()</c> — so
    /// folding them there would give a parsed default that never matches the extracted one,
    /// i.e. a column re-diffing on every deploy.
    /// </summary>
    public abstract bool LocalTimeIsCurrentTimestampSynonym { get; }

    /// <summary>
    /// Whether <c>CURDATE()</c> / <c>CURRENT_DATE</c> and <c>CURTIME()</c> / <c>CURRENT_TIME</c>
    /// are valid column <c>DEFAULT</c>s, each keeping its own canonical token.
    ///
    /// MariaDB accepts them and stores them under their own names. MySQL rejects them outright
    /// with a <em>syntax error</em> — not merely an invalid value — so a build targeting MySQL
    /// must leave them unmodeled and warn, rather than emit DDL the server cannot parse.
    /// </summary>
    public abstract bool SupportsDateAndTimeFunctionDefaults { get; }

    /// <summary>
    /// Whether the engine supports functional (expression) index keys —
    /// <c>CREATE INDEX ix ON t ((a + b))</c> — and so reports them in
    /// <c>information_schema.STATISTICS.EXPRESSION</c> (issue #161).
    ///
    /// MySQL has had them since 8.0.13. MariaDB has none: it rejects the DDL at the server with
    /// a syntax error, and its <c>STATISTICS</c> has no <c>EXPRESSION</c> column at all — so
    /// naming that column in the extractor's query is itself an unknown-column error there,
    /// which is why the query is built around this capability rather than selecting it always.
    /// </summary>
    public abstract bool SupportsFunctionalIndexKeys { get; }
}

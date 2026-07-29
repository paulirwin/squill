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
}

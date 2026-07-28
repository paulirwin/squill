namespace Squill.Provider.MariaDb;

/// <summary>
/// Which of the two engines this provider serves is being targeted. One provider covers both
/// (see <see cref="MariaDbSquillProvider"/>), and for almost everything they agree — but a few
/// constructs genuinely mean different things on each, so the build has to know which it is
/// building for rather than assuming.
///
/// The current-timestamp family of column <c>DEFAULT</c>s is the case that forced this
/// distinction (issue #147). Measured against <c>mariadb:latest</c> and <c>mysql:latest</c>,
/// the same source text is stored differently — and in one case means something else entirely:
///
/// <list type="table">
///   <listheader><term>Source</term><description>MariaDB / MySQL</description></listheader>
///   <item>
///     <term><c>LOCALTIME</c></term>
///     <description><c>curtime()</c> — a <em>time of day</em> — versus
///       <c>CURRENT_TIMESTAMP</c>, a true synonym</description>
///   </item>
///   <item>
///     <term><c>LOCALTIMESTAMP</c></term>
///     <description><c>localtimestamp()</c> versus <c>CURRENT_TIMESTAMP</c></description>
///   </item>
///   <item>
///     <term><c>CURDATE()</c> / <c>CURTIME()</c></term>
///     <description><c>curdate()</c> / <c>curtime()</c> versus a <em>syntax error</em></description>
///   </item>
/// </list>
///
/// Canonicalizing those without knowing the engine would give one of the two a default that
/// never matches what its catalog reports back — a column re-diffing on every deploy, forever.
/// That is why <see cref="MariaDbDefaultValue"/> and <see cref="ParserWorkspaceModelBuilder"/>
/// take this as a <em>required</em> input rather than defaulting to one engine: a missed call
/// site would otherwise be a silent wrong-semantics bug rather than a compile error.
/// </summary>
public enum MariaDbEngine
{
    /// <summary>MariaDB, in any supported major version.</summary>
    MariaDb,

    /// <summary>MySQL, in any supported major version.</summary>
    MySql,
}

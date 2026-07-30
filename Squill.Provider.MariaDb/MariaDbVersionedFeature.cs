namespace Squill.Provider.MariaDb;

/// <summary>
/// A construct whose availability depends on which engine of this family is targeted and which
/// major version of it (issue #142), so a build can warn that the source will not deploy there
/// rather than letting the server reject it partway through a deploy.
///
/// <para>
/// The two engines this provider serves diverge on every construct here, which is why a
/// feature carries a minimum version <em>per engine</em> rather than one number: <c>VECTOR</c>
/// arrived in MySQL 9 and in MariaDB 11, so the same source is fine on one engine's major and
/// too new on the other's. A feature may also be absent from an engine entirely — MySQL has no
/// <c>UUID</c> type at any version — which is a different fact from "too new" and reported
/// under its own code, since there is no version to upgrade to.
/// </para>
///
/// <para>
/// Which engine is being targeted is asked of the schema provider, never tested for by name,
/// so this follows the same rule as every other capability in this assembly. The minimum
/// versions themselves are stated here rather than on the provider because they are facts about
/// a construct, not about an engine: putting them on the provider would mean every new feature
/// added a member to five classes.
/// </para>
///
/// <para>
/// Squill models a target as a <em>major</em> version only, which limits what can be gated. A
/// construct introduced in a point release (MariaDB's <c>UUID</c> in 10.7, MySQL's functional
/// indexes in 8.0.13) cannot be distinguished from the rest of its major, so it is either
/// reported against the whole major or not at all. Where the construct is the more recent half
/// of a long-lived major — <c>UUID</c> against MariaDB 10, whose earlier point releases far
/// outnumber 10.7+ — reporting it is the useful answer, and the warning names the point release
/// so an author on 10.7+ can see the check is coarser than their server.
/// </para>
/// </summary>
/// <param name="Description">
/// How the construct is named in the warning, spelled as it appears in source.
/// </param>
/// <param name="MariaDbMinimumMajorVersion">
/// The first MariaDB major that accepts it, or null if MariaDB has no equivalent at any version.
/// </param>
/// <param name="MySqlMinimumMajorVersion">
/// The first MySQL major that accepts it, or null if MySQL has no equivalent at any version.
/// </param>
/// <param name="DocumentationUrl">
/// The official page establishing the versions, cited in the warning itself — the author's next
/// question after "this is too new" is always "says who?".
/// </param>
/// <param name="Note">
/// An optional clarification appended to the warning, for a construct whose real boundary is a
/// point release the major-only target cannot express.
/// </param>
public readonly record struct MariaDbVersionedFeature(
    string Description,
    int? MariaDbMinimumMajorVersion,
    int? MySqlMinimumMajorVersion,
    string DocumentationUrl,
    string? Note = null)
{
    /// <summary>
    /// The <c>VECTOR</c> column type.
    ///
    /// MariaDB: "VECTOR is available from MariaDB 11.7.1"
    /// (https://mariadb.com/docs/server/reference/data-types/vectors/vector). MySQL: added in
    /// 9.0 — "Support is added in this release for a VECTOR column type"
    /// (https://dev.mysql.com/doc/relnotes/mysql/9.0/en/news-9-0-0.html).
    ///
    /// Both engines spell the type the same way and neither accepts it before those versions,
    /// but they are otherwise not portable: MariaDB indexes it with <c>VECTOR INDEX</c>, while
    /// MySQL forbids indexing a <c>VECTOR</c> at all. Only the type itself is gated here.
    /// </summary>
    public static readonly MariaDbVersionedFeature Vector = new(
        "VECTOR",
        MariaDbMinimumMajorVersion: 11,
        MySqlMinimumMajorVersion: 9,
        "https://mariadb.com/docs/server/reference/data-types/vectors/vector");

    /// <summary>
    /// The native <c>UUID</c> column type, which MySQL has no equivalent of at any version —
    /// on MySQL the idiom is <c>BINARY(16)</c>, a different declaration Squill cannot infer.
    ///
    /// "UUID is available from MariaDB 10.7"
    /// (https://mariadb.com/docs/server/reference/data-types/string-data-types/uuid-data-type).
    /// 10.7 is a point release inside major 10, which a major-only target cannot express — see
    /// the note on this type for how that is handled.
    /// </summary>
    public static readonly MariaDbVersionedFeature Uuid = new(
        "UUID",
        MariaDbMinimumMajorVersion: 11,
        MySqlMinimumMajorVersion: null,
        "https://mariadb.com/docs/server/reference/data-types/string-data-types/uuid-data-type",
        Note: "MariaDB accepts it from 10.7; Squill targets a major version only, so a project "
            + "on 10.7 or later can suppress this warning or raise its target version.");

    /// <summary>
    /// The minimum major version of <paramref name="schemaProvider"/>'s engine that accepts this
    /// construct, or null if that engine has no equivalent at any version. Asks the provider
    /// which engine it is rather than testing its name, so a new engine in this family answers
    /// the question by declaring a capability like any other.
    /// </summary>
    public int? MinimumMajorVersionFor(MariaDbFamilyDatabaseSchemaProvider schemaProvider)
        => schemaProvider.IsMySql ? MySqlMinimumMajorVersion : MariaDbMinimumMajorVersion;
}

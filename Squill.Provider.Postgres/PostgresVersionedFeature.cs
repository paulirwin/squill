namespace Squill.Provider.Postgres;

/// <summary>
/// A PostgreSQL construct that a given major version introduced, so a build targeting an older
/// major can warn that the source will not deploy there (issue #142). Until now the target
/// version was checked only at deploy time against the server's reported version, which meant
/// too-new source built cleanly and then failed as a syntax error partway through the deploy,
/// after earlier statements had already been applied.
///
/// <para>
/// Every entry states the version that introduced the construct and cites the documentation
/// that says so. The citation is the point: a version boundary guessed from the grammar is
/// worthless here, because the ANTLR grammar is a single dialect with no version dimension at
/// all — it accepts <c>NULLS NOT DISTINCT</c> regardless of the target, which is exactly why
/// this table has to exist separately from the parser.
/// </para>
///
/// <para>
/// The bar for an entry is that the construct is a <em>grammar</em> gate — a syntax error on
/// the older major, not merely an unsupported option or a semantic rejection. Constructs that
/// parse on the older version and fail later at name resolution (identity columns on
/// partitioned tables, a <c>security_invoker</c> view option) are deliberately absent: catching
/// those needs context this pass does not have, and a half-right check that quietly misses
/// cases is worse than none. The same goes for any construct the syntax tree cannot currently
/// represent — <c>GENERATED … VIRTUAL</c> (PostgreSQL 18) is not here because the vendored
/// grammar has no <c>VIRTUAL</c> alternative, so there is nothing to detect.
/// </para>
/// </summary>
/// <param name="Description">
/// How the construct is named in the warning, spelled as it appears in source (e.g.
/// <c>NULLS NOT DISTINCT</c>) so the message points at something the author can search for.
/// </param>
/// <param name="MinimumMajorVersion">The first PostgreSQL major version that accepts it.</param>
/// <param name="DocumentationUrl">
/// The official page establishing the version. Carried on the record rather than left in a
/// comment so the warning itself can cite it — the author's next question after "this is too
/// new" is always "says who?".
/// </param>
public readonly record struct PostgresVersionedFeature(
    string Description,
    int MinimumMajorVersion,
    string DocumentationUrl)
{
    /// <summary>
    /// <c>UNIQUE … NULLS NOT DISTINCT</c> on a unique index or constraint, which makes NULLs
    /// collide with each other instead of being all-distinct (issue #160).
    ///
    /// PostgreSQL 15 release notes: "Previously NULL entries were always treated as distinct
    /// values, but this can now be changed by creating constraints and indexes using UNIQUE
    /// NULLS NOT DISTINCT." The PostgreSQL 14 <c>CREATE TABLE</c> synopsis has no
    /// <c>NULLS [NOT] DISTINCT</c> in its <c>UNIQUE</c> clause at all, so on 14 this is a
    /// syntax error rather than an ignored option — the deploy fails, it does not silently
    /// deploy the opposite uniqueness semantics.
    /// </summary>
    public static readonly PostgresVersionedFeature NullsNotDistinct = new(
        "NULLS NOT DISTINCT",
        15,
        "https://www.postgresql.org/docs/release/15.0/");
}

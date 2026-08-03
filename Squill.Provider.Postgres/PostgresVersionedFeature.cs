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

    /// <summary>
    /// A non-decimal integer literal — <c>0x19</c>, <c>0o17</c>, <c>0b101</c> — anywhere an
    /// expression reaches the model: a column <c>DEFAULT</c>, a <c>CHECK</c> predicate (column- or
    /// table-level), a generated column's generation expression, or an index predicate
    /// (issue #191).
    ///
    /// PostgreSQL 16 release notes: "Allow non-decimal integer literals". Measured against pinned
    /// servers rather than taken from the notes alone, because the interesting half of the claim
    /// is what the OLDER versions do, and they do not agree with each other: 15 rejects
    /// <c>DEFAULT 0x19</c> outright ("trailing junk after numeric literal"), but 14 does not
    /// necessarily fail at all — it lexes <c>0x19</c> as the integer <c>0</c> followed by the
    /// identifier <c>x19</c>, so <c>SELECT 0x1f</c> quietly returns <c>0</c> there. A construct
    /// that returns the wrong number rather than an error is exactly the kind this warning
    /// exists to catch before a deploy.
    ///
    /// <para>
    /// Only the spelling is version-gated, never the value. PostgreSQL normalizes the radix away
    /// when it stores either form (measured on 16: <c>DEFAULT 0x19</c> reads back from
    /// <c>column_default</c> as <c>25</c>, and <c>GENERATED ALWAYS AS (m + 0x19)</c> reads back
    /// from <c>generation_expression</c> as <c>(m + 25)</c>), so the round trip is unaffected in
    /// every position and no deploy re-diffs because of one.
    /// </para>
    ///
    /// <para>
    /// What differs between positions is what Squill emits when it CREATES the object, and that is
    /// what this warning is about. A <c>DEFAULT</c> is canonicalized to the stored value on its way
    /// into the model, so it deploys as <c>25</c> whatever the source said; a generation expression
    /// and an index predicate are rendered back out with the literal's source spelling, so those
    /// really do carry <c>0x19</c> to a server that would reject it.
    /// </para>
    /// </summary>
    public static readonly PostgresVersionedFeature NonDecimalIntegerLiteral = new(
        "a non-decimal integer literal",
        16,
        "https://www.postgresql.org/docs/release/16.0/");
}

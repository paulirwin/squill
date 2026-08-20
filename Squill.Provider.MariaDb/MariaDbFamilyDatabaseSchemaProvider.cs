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
    protected MariaDbFamilyDatabaseSchemaProvider()
    {
    }

    protected MariaDbFamilyDatabaseSchemaProvider(TargetVersion? targetVersion)
        : base(targetVersion)
    {
    }

    /// <summary>
    /// Whether <em>authoring</em> a functional index key is safe for the declared target floor.
    ///
    /// <para>
    /// Distinct from <see cref="SupportsFunctionalIndexKeys"/>, and deliberately so. That one
    /// answers a question about the server actually in front of us — does this catalog have a
    /// <c>STATISTICS.EXPRESSION</c> column to select? — and is resolved from the live version
    /// banner during extraction, where reading it from a project's declared floor would build
    /// catalog SQL for the wrong server and fail with an unknown-column error. This one answers
    /// the build-time question instead: is the construct present in every release at or after the
    /// floor?
    /// </para>
    ///
    /// <para>
    /// The default is the conservative one — whatever the engine supports at all, with no
    /// point-release qualification. An engine that introduced the construct partway through a
    /// major overrides this to state its own threshold, so the version number lives beside the
    /// engine it describes rather than in a family base that serves both.
    /// </para>
    /// </summary>
    public virtual bool CanAuthorFunctionalIndexKeys => SupportsFunctionalIndexKeys;

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

    /// <summary>
    /// The storage engine a table gets when its CREATE TABLE names none, used to decide whether a
    /// declared <c>ENGINE</c> is worth recording (issue #207).
    ///
    /// <para>
    /// A capability rather than a constant in the model builder because it is the build-time
    /// half of a question the extractor answers from the live server
    /// (<c>information_schema.ENGINES</c> where <c>SUPPORT = 'DEFAULT'</c>): both sides have to
    /// reach the same answer or a table declaring the default engine would record an option its
    /// extracted counterpart omits, and re-diff forever.
    /// </para>
    ///
    /// <para>
    /// Virtual rather than abstract, unlike most members here, because this is not a measured
    /// divergence: both engines have defaulted to InnoDB since MySQL 5.5 and MariaDB 5.5, and
    /// <c>default_storage_engine</c> is settable per server, so the value is a well-known default
    /// rather than an engine trait. An engine whose default moves overrides it.
    /// </para>
    /// </summary>
    public virtual string DefaultStorageEngine => "InnoDB";

    /// <summary>
    /// The collation a table inherits when neither it nor its schema names one (issue #207).
    ///
    /// <para>
    /// A declared <c>COLLATE</c> equal to this is the one table option that cannot round-trip: a
    /// table declaring its schema's default collation and one declaring nothing are
    /// byte-identical in <c>information_schema</c> (measured), so the extractor cannot tell them
    /// apart and records neither. The build matches that by not recording it either, which keeps
    /// the two models agreeing; what the table is collated as is unaffected, since the server
    /// applies the same collation either way.
    /// </para>
    ///
    /// <para>
    /// Abstract because the answer is a measured divergence: MariaDB 12 reports
    /// <c>utf8mb4_uca1400_ai_ci</c> where MySQL 9 reports <c>utf8mb4_0900_ai_ci</c>. It is
    /// necessarily best-effort, since the default is configurable per server and per schema and a
    /// build has neither in front of it; a target whose default has been changed falls back to
    /// the ordinary case of a collation that differs from it, which does round-trip.
    /// </para>
    /// </summary>
    public abstract string DefaultCollation { get; }

    /// <summary>
    /// The collation a column takes from a bare <c>CHARACTER SET</c> naming
    /// <c>utf8mb3</c> or <c>utf8mb4</c>, as a <c>(utf8mb3, utf8mb4)</c> pair (issue #217).
    ///
    /// <para>
    /// Abstract, and split out from the character sets below, because these two are the ones
    /// that move. Measured across the supported majors, <c>utf8mb4</c> defaults to
    /// <c>utf8mb4_general_ci</c> on MariaDB 10, <c>utf8mb4_uca1400_ai_ci</c> on MariaDB 11 and
    /// 12, and <c>utf8mb4_0900_ai_ci</c> on MySQL 8 and 9: three answers across four majors, so
    /// there is no shared rule to fall back on and each has to state its own.
    /// </para>
    /// </summary>
    protected abstract (string Utf8Mb3, string Utf8Mb4) DefaultUnicodeCollations { get; }

    /// <summary>
    /// The collation a column inherits from a type-level <c>CHARACTER SET</c>, or <c>null</c>
    /// when Squill has no measured answer for that character set (issue #217).
    ///
    /// <para>
    /// This exists because a character set is not something the model can hold: both engines
    /// resolve <c>CHARACTER SET x</c> to a collation at creation time and
    /// <c>information_schema</c> reports only the result, so a charset that reached the model as
    /// itself could never be compared against an extracted column. Resolving it here is what
    /// lets the declared spelling round-trip as the thing the server actually stored.
    /// </para>
    ///
    /// <para>
    /// An unrecognized character set returns <c>null</c> rather than a guess. The obvious
    /// guess (<c>&lt;charset&gt;_general_ci</c>) is wrong for several of the sets measured
    /// above, and a wrong collation is worse than none: it deploys a column that sorts
    /// differently from the declaration, where recording nothing merely leaves the server to
    /// apply its own default. The caller warns instead.
    /// </para>
    /// </summary>
    public virtual string? DefaultCollationForCharacterSet(string characterSet)
        => characterSet.ToLowerInvariant() switch
        {
            // Measured identical on MariaDB 10/11/12 and MySQL 8/9, so they are stated once
            // here rather than five times. A future major that changes one overrides this.
            "latin1" => "latin1_swedish_ci",
            "ascii" => "ascii_general_ci",
            "binary" => "binary",
            "utf8mb3" or "utf8" => DefaultUnicodeCollations.Utf8Mb3,
            "utf8mb4" => DefaultUnicodeCollations.Utf8Mb4,
            _ => null,
        };

    /// <summary>
    /// The collation a <c>BINARY</c> type suffix selects, given the collation the column would
    /// otherwise have had (issue #217).
    ///
    /// <para>
    /// <c>BINARY</c> is not a character set or a type of its own: it selects the binary
    /// collation of whichever character set is already in play. Measured, that character set is
    /// the collation's own prefix, in a <c>COLLATE=latin1_general_ci</c> table a
    /// <c>VARCHAR(10) BINARY</c> column reports <c>latin1_bin</c>, so the suffix is resolved by
    /// swapping the collation's tail for <c>_bin</c>.
    /// </para>
    /// </summary>
    public virtual string BinaryCollationFor(string collation)
    {
        var separator = collation.IndexOf('_');

        // A collation with no underscore is already a whole character set name standing in for
        // its own collation (`binary`), so there is no tail to replace.
        return separator < 0 ? collation : $"{collation[..separator]}_bin";
    }

    /// <summary>
    /// Whether the engine has sequences at all (issue #218): <c>CREATE SEQUENCE</c>,
    /// <c>NEXTVAL()</c> and the <c>SEQUENCE</c> table type.
    ///
    /// <para>
    /// A hard divergence rather than a reporting one: MariaDB has had them since 10.3, while
    /// MySQL 9.7 rejects the statement with <c>ERROR 1064</c>, a <em>syntax error</em>
    /// (measured). So unlike <see cref="ReportsViewAlgorithm"/>, where the construct works and
    /// only the catalog is blind, here there is nothing to deploy to: a project declaring a
    /// sequence against MySQL is an error at build time rather than an unmodeled warning,
    /// because emitting the DDL would fail at the server.
    /// </para>
    /// </summary>
    public abstract bool SupportsSequences { get; }

    /// <summary>
    /// Whether the engine reports a view's <c>ALGORITHM</c> back through
    /// <c>information_schema.VIEWS</c>, and so whether a declared one can be modeled
    /// (issue #208).
    ///
    /// <para>
    /// Measured: MariaDB has an <c>ALGORITHM</c> column there and reports <c>MERGE</c>,
    /// <c>TEMPTABLE</c> or <c>UNDEFINED</c> per view. MySQL's <c>VIEWS</c> has no such column
    /// at all, so naming it in the extractor's query is an unknown-column error on that engine
    /// (the same shape of divergence as <see cref="SupportsFunctionalIndexKeys"/>), and why
    /// the query is built around this capability rather than always selecting it.
    /// </para>
    ///
    /// <para>
    /// MySQL does still honour a declared <c>ALGORITHM</c> and echoes it from
    /// <c>SHOW CREATE VIEW</c>, so this is a statement about what the catalog can be asked for,
    /// not about what the engine supports. Where it cannot be read back it is left unmodeled
    /// and warned about, because a facet the extractor cannot see would re-diff on every
    /// deploy.
    /// </para>
    /// </summary>
    public abstract bool ReportsViewAlgorithm { get; }

    /// <summary>
    /// How the engine spells "the optimizer should ignore this index", and how it reports it
    /// back (issue #211).
    ///
    /// <para>
    /// Measured, this is a hard divergence in both directions: MySQL takes <c>INVISIBLE</c> and
    /// reports <c>information_schema.STATISTICS.IS_VISIBLE</c>; MariaDB takes <c>IGNORED</c>
    /// and reports <c>STATISTICS.IGNORED</c>. Each engine rejects the other's keyword with a
    /// <em>syntax error</em>, and neither has the other's catalog column, so naming the wrong
    /// one in the extractor's query is itself an unknown-column error, the same shape of
    /// divergence as <see cref="SupportsFunctionalIndexKeys"/>.
    /// </para>
    ///
    /// <para>
    /// Null for an engine that has neither. Abstract because there is no safe default: guessing
    /// would emit DDL one of the two engines cannot parse.
    /// </para>
    /// </summary>
    public abstract IndexVisibilityStyle? IndexVisibility { get; }
}

/// <summary>
/// The spelling and catalog column an engine uses for index visibility (issue #211). Two
/// members rather than a bool so the keyword and the column that reports it stay together:
/// they always vary as a pair.
/// </summary>
public enum IndexVisibilityStyle
{
    /// <summary>MySQL: <c>INVISIBLE</c> / <c>VISIBLE</c>, read from <c>IS_VISIBLE</c>.</summary>
    Invisible,

    /// <summary>MariaDB: <c>IGNORED</c> / <c>NOT IGNORED</c>, read from <c>IGNORED</c>.</summary>
    Ignored,
}

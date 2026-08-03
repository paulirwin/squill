namespace Squill.Provider.MariaDb;

/// <summary>
/// A construct every supported version of an engine still accepts, but whose documentation says
/// it is scheduled for removal (issue #190) — reported as SQ1006.
///
/// <para>
/// SQ1006 covers non-recommendation as well as scheduled removal, but every entry in this type is
/// the latter: each cites a MySQL page saying outright to expect the construct's support to be
/// removed. That is why the warning built here states a removal, which the Postgres side's does
/// not — <c>time with time zone</c> is advised against without ever being promised an end.
/// </para>
///
/// <para>
/// This is a different axis from <see cref="MariaDbVersionedFeature"/>, which is why it is a
/// separate type rather than another field on that one. There the question is whether the target
/// version is old enough to be a problem, and the remedy is to raise it; here the target version
/// is not implicated at all — the construct works on every version in the supported window,
/// newest included — and the remedy is to change the source. Reusing the version machinery would
/// mean expressing "deprecated" as a minimum version, which is exactly the nonsense message the
/// issue set out to avoid.
/// </para>
///
/// <para>
/// Deprecation is nonetheless <em>per engine</em>, for the same reason versions are: this provider
/// serves two engines with independent release policies, and every construct recorded here is
/// deprecated by MySQL and not by MariaDB. Measured against the vendors' own pages, not inferred
/// from one engine's behaviour: MySQL deprecated <c>ZEROFILL</c>, integer display widths, float
/// <c>UNSIGNED</c> and float <c>AUTO_INCREMENT</c> in 8.0.17, while MariaDB's Knowledge Base
/// documents all four as ordinary current functionality with no removal language. Warning a
/// MariaDB project about MySQL's plans would be a claim its documentation does not support.
/// </para>
///
/// <para>
/// A construct is recorded here only when the vendor states removal or non-recommendation
/// outright. Advice to prefer something else is not deprecation — issue #190 lists candidates
/// checked and deliberately excluded on exactly that ground — because a code that fires on
/// merely dated-looking SQL is one authors learn to suppress wholesale.
/// </para>
/// </summary>
/// <param name="Description">
/// How the construct is named in the warning, spelled as it appears in source.
/// </param>
/// <param name="Remedy">
/// What to write instead, stated concretely. A deprecation warning differs from a version warning
/// in that raising a number will never resolve it, so the message has to carry the alternative or
/// the author is left with a complaint and no action.
/// </param>
/// <param name="DeprecatedByMySql">Whether MySQL documents this as deprecated.</param>
/// <param name="DeprecatedByMariaDb">Whether MariaDB documents this as deprecated.</param>
/// <param name="MySqlDocumentationUrl">
/// The MySQL page stating the deprecation, cited in the warning itself — the author's next
/// question after "this is going away" is always "says who?".
/// </param>
/// <param name="MariaDbDocumentationUrl">
/// The MariaDB page stating the deprecation, or null when MariaDB does not deprecate it. Never a
/// substitute for the other engine's: citing MySQL's page in a MariaDB warning would point at a
/// document that says nothing about MariaDB.
/// </param>
/// <param name="Note">
/// An optional clarification appended to the warning, for a deprecation with a consequence the
/// bare statement does not convey — a version that has already made it an error, say.
/// </param>
public readonly record struct MariaDbDeprecatedFeature(
    string Description,
    string Remedy,
    bool DeprecatedByMySql,
    bool DeprecatedByMariaDb,
    string? MySqlDocumentationUrl = null,
    string? MariaDbDocumentationUrl = null,
    string? Note = null)
{
    private const string NumericTypeAttributesUrl =
        "https://dev.mysql.com/doc/refman/8.0/en/numeric-type-attributes.html";

    /// <summary>
    /// The <c>ZEROFILL</c> attribute on a numeric column.
    ///
    /// "As of MySQL 8.0.17, the ZEROFILL attribute is deprecated for numeric data types, as is the
    /// display width attribute for integer data types. […] You should expect support for ZEROFILL
    /// and display widths for integer data types to be removed in a future version of MySQL."
    /// (https://dev.mysql.com/doc/refman/8.0/en/numeric-type-attributes.html)
    ///
    /// MariaDB's Knowledge Base documents ZEROFILL as current functionality with no removal
    /// language, so it is not reported there.
    /// </summary>
    public static readonly MariaDbDeprecatedFeature Zerofill = new(
        "ZEROFILL",
        "pad the value when formatting it for display instead",
        DeprecatedByMySql: true,
        DeprecatedByMariaDb: false,
        MySqlDocumentationUrl: NumericTypeAttributesUrl);

    /// <summary>
    /// A display width on an integer type, e.g. <c>INT(11)</c>. Deprecated by the same MySQL
    /// sentence as <see cref="Zerofill"/>, and likewise not by MariaDB.
    ///
    /// The width never constrained the range stored — <c>INT(11)</c> and <c>INT</c> hold exactly
    /// the same values — so dropping it changes nothing about the column, which is what makes the
    /// remedy safe to state outright.
    /// </summary>
    public static readonly MariaDbDeprecatedFeature IntegerDisplayWidth = new(
        "an integer display width",
        "drop the width: it never constrained the range stored, so INT(11) and INT hold the "
            + "same values",
        DeprecatedByMySql: true,
        DeprecatedByMariaDb: false,
        MySqlDocumentationUrl: NumericTypeAttributesUrl);

    /// <summary>
    /// <c>UNSIGNED</c> on an approximate or fixed-point numeric type.
    ///
    /// "As of MySQL 8.0.17, the UNSIGNED attribute is deprecated for columns of type FLOAT, DOUBLE,
    /// and DECIMAL (and any synonyms) and you should expect support for it to be removed in a
    /// future version of MySQL."
    /// (https://dev.mysql.com/doc/refman/8.0/en/numeric-type-attributes.html)
    ///
    /// Scoped to those three and their synonyms: UNSIGNED on an <em>integer</em> type is not
    /// deprecated, and is the single most common attribute in MySQL schemas.
    /// </summary>
    public static readonly MariaDbDeprecatedFeature FloatingPointUnsigned = new(
        "UNSIGNED on a FLOAT, DOUBLE or DECIMAL column",
        "drop UNSIGNED and enforce non-negativity with a CHECK constraint",
        DeprecatedByMySql: true,
        DeprecatedByMariaDb: false,
        MySqlDocumentationUrl: NumericTypeAttributesUrl);

    /// <summary>
    /// <c>AUTO_INCREMENT</c> on a <c>FLOAT</c> or <c>DOUBLE</c> column.
    ///
    /// "As of MySQL 8.0.17, AUTO_INCREMENT support is deprecated for FLOAT and DOUBLE columns; you
    /// should expect it to be removed in a future version of MySQL."
    /// (https://dev.mysql.com/doc/refman/8.0/en/numeric-type-attributes.html)
    ///
    /// The note earns its place here: MySQL 8.4 already rejects this outright, so unlike the other
    /// three this is not a future problem for a project deploying to a current server. Squill's
    /// target is a major version, which cannot distinguish 8.0 from 8.4, so the deprecation
    /// warning is the honest report and the note says what an 8.4 server will actually do.
    /// </summary>
    public static readonly MariaDbDeprecatedFeature FloatingPointAutoIncrement = new(
        "AUTO_INCREMENT on a FLOAT or DOUBLE column",
        "use an integer type for the auto-incrementing column",
        DeprecatedByMySql: true,
        DeprecatedByMariaDb: false,
        MySqlDocumentationUrl: NumericTypeAttributesUrl,
        Note: "MySQL 8.4 rejects it outright rather than deprecating it.");

    /// <summary>
    /// The <c>utf8</c> character set, an alias whose meaning differs between the two engines and
    /// is deprecated on one of them.
    ///
    /// "The utf8mb3 character set is deprecated. […] Expect utf8mb3 to be removed in a future major
    /// release of MySQL." (https://dev.mysql.com/doc/refman/8.0/en/charset-unicode-utf8mb3.html)
    /// On MySQL, <c>utf8</c> is an alias for <c>utf8mb3</c>, so declaring it declares the
    /// deprecated set.
    ///
    /// MariaDB does not deprecate it: <c>utf8</c> is an alias for <c>utf8mb3</c> there too, "but
    /// this can be changed to utf8mb4 by changing the default value of the old_mode system
    /// variable" (https://mariadb.com/kb/en/unicode/) — a configurable meaning rather than a
    /// scheduled removal. That is a portability hazard, but it is not what SQ1006 reports.
    /// </summary>
    public static readonly MariaDbDeprecatedFeature Utf8CharacterSet = new(
        "the utf8 character set, an alias for the deprecated utf8mb3",
        "declare utf8mb4, which stores the full range of Unicode",
        DeprecatedByMySql: true,
        DeprecatedByMariaDb: false,
        MySqlDocumentationUrl:
            "https://dev.mysql.com/doc/refman/8.0/en/charset-unicode-utf8mb3.html");

    /// <summary>
    /// Whether <paramref name="schemaProvider"/>'s engine documents this construct as deprecated.
    /// Asks the provider which engine it is rather than testing its name, so a new engine in this
    /// family answers by declaring a capability like any other.
    /// </summary>
    public bool IsDeprecatedBy(MariaDbFamilyDatabaseSchemaProvider schemaProvider)
        => schemaProvider.IsMySql ? DeprecatedByMySql : DeprecatedByMariaDb;

    /// <summary>
    /// The documentation stating <paramref name="schemaProvider"/>'s engine's deprecation, or null
    /// when that engine has none to cite. Per-engine for the same reason
    /// <see cref="IsDeprecatedBy"/> is: each vendor deprecates on its own schedule and documents
    /// it on its own page.
    /// </summary>
    public string? DocumentationUrlFor(MariaDbFamilyDatabaseSchemaProvider schemaProvider)
        => schemaProvider.IsMySql ? MySqlDocumentationUrl : MariaDbDocumentationUrl;
}

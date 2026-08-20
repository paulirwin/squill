namespace Squill.Provider.MariaDb;

/// <summary>
/// The values a MariaDB sequence takes when its <c>CREATE SEQUENCE</c> omits them (issue #218).
///
/// <para>
/// These are what make the omit-when-default convention work for sequences. A sequence's
/// backing table always reports every option with its defaults filled in, so the build must
/// know the same defaults the server applies: an option equal to its default is not stored on
/// either side, and one that differs is stored on both. Get a value wrong here and every
/// sequence declaring it re-diffs on every deploy.
/// </para>
///
/// <para>
/// Measured against <c>mariadb:latest</c> (12.3.2) via <c>SHOW CREATE SEQUENCE</c>, not read
/// from the manual, and deliberately <em>not</em> shared with the Postgres provider's
/// <c>PostgresIdentitySequenceDefaults</c> despite modeling the same concept. The two engines
/// genuinely disagree: MariaDB caches 1000 by default where Postgres caches 1, and its bounds
/// stop one short of the int64 extremes rather than reaching them.
/// </para>
/// </summary>
internal static class MariaDbSequenceDefaults
{
    public const long Increment = 1;

    /// <summary>
    /// Measured: a bare <c>CREATE SEQUENCE</c> reports <c>cache 1000</c>. Notably not 1, which
    /// is the Postgres default and the value this would take if copied from that provider.
    /// </summary>
    public const long CacheSize = 1000;

    public const bool IsCycling = false;

    /// <summary>
    /// The default bounds for an ascending sequence of the given backing type.
    ///
    /// <para>
    /// The ceiling is one <em>below</em> the type's maximum: measured, a bare bigint sequence
    /// reports <c>maxvalue 9223372036854775806</c>, not <c>…807</c>. MariaDB reserves the top
    /// value to mark exhaustion, so using the type maximum here would leave every sequence
    /// recording a MaxValue the extractor reports differently.
    /// </para>
    ///
    /// <para>
    /// Only the ascending case is computed. A descending sequence needs
    /// <c>INCREMENT BY -1</c>, which the vendored grammar cannot parse (see
    /// CreateSequenceTests), so its defaults could not be reached or tested from a build; the
    /// extractor handles a descending sequence created outside Squill through the ordinary
    /// differs-from-default path.
    /// </para>
    /// </summary>
    public static (long StartValue, long MinValue, long MaxValue) For(string canonicalTypeName)
    {
        var typeMax = canonicalTypeName switch
        {
            "tinyint" => (long)sbyte.MaxValue,
            "smallint" => (long)short.MaxValue,
            "mediumint" => 8388607L,
            "int" => int.MaxValue,
            _ => long.MaxValue,
        };

        return (1, 1, typeMax - 1);
    }

    /// <summary>
    /// The type a sequence takes when no <c>AS</c> clause is written. The clause itself cannot
    /// be authored (<c>sequenceSpec</c> has no <c>AS</c> alternative in the vendored grammar),
    /// so this is the only type a Squill-built sequence has, though the extractor still reads
    /// the real one so a sequence created outside Squill is not silently re-typed.
    /// </summary>
    public const string DefaultSequenceTypeName = "bigint";
}

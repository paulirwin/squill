namespace Squill.Provider.Postgres;

/// <summary>
/// The default sequence-option values Postgres applies to a sequence, by its canonical type
/// name and direction (see CREATE SEQUENCE: an ascending sequence defaults to minvalue 1,
/// maxvalue = type max, start = minvalue; a descending one to maxvalue -1, minvalue = type
/// min, start = maxvalue).
///
/// Shared by identity columns (issue #13) and standalone sequences (issue #122), which obey
/// the same rules — the one difference being the type they default to when none is written:
/// an identity column's sequence takes the column's type, while a declared sequence defaults
/// to <see cref="DefaultSequenceTypeName"/>.
///
/// Both model builders share these so they agree on which options are worth storing:
/// the parser builder omits options equal to the default, and the DB builder omits
/// extracted values equal to the default (information_schema reports every option with
/// defaults filled in), keeping parsed and extracted models hash-identical — the same
/// omit-when-default convention as IsNullable and numeric precision.
/// </summary>
internal static class PostgresIdentitySequenceDefaults
{
    public const long Increment = 1;
    public const long CacheSize = 1;
    public const bool IsCycling = false;

    /// <summary>
    /// The type a standalone <c>CREATE SEQUENCE</c> takes when its <c>AS</c> clause is
    /// omitted. Note this is <c>bigint</c>, not the <c>integer</c> a bare <c>serial</c>
    /// column produces — the two defaults genuinely differ.
    /// </summary>
    public const string DefaultSequenceTypeName = "bigint";

    /// <summary>
    /// The default START/MINVALUE/MAXVALUE for an identity column of the given canonical
    /// type (<c>smallint</c>, <c>integer</c>, <c>bigint</c>), honoring the sequence
    /// direction implied by <paramref name="increment"/>.
    /// </summary>
    public static (long StartValue, long MinValue, long MaxValue) For(string canonicalTypeName, long increment)
    {
        var (typeMin, typeMax) = canonicalTypeName switch
        {
            "smallint" => ((long)short.MinValue, (long)short.MaxValue),
            "bigint" => (long.MinValue, long.MaxValue),
            // Identity is only valid on smallint/integer/bigint; anything else would be
            // rejected by Postgres at deploy, so integer bounds are a harmless fallback.
            _ => ((long)int.MinValue, (long)int.MaxValue),
        };

        return increment >= 0
            ? (1, 1, typeMax)
            : (-1, typeMin, -1);
    }
}

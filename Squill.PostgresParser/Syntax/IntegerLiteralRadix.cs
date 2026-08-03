namespace Squill.PostgresParser.Syntax;

/// <summary>
/// The base an integer literal was WRITTEN in, which is a fact about the source spelling rather
/// than about its value: <c>0x19</c> and <c>25</c> are the same number, but only the first
/// requires PostgreSQL 16 (issue #191).
///
/// <para>
/// Recorded on <see cref="LiteralExpression"/> because the parser already knows it from the token
/// type (<c>HexadecimalIntegral</c> and friends), and the alternative — re-scanning the literal's
/// text downstream for a <c>0x</c> prefix — would re-decide something the grammar had already
/// settled, in a place where a string constant containing "0x" could be mistaken for one.
/// </para>
/// </summary>
public enum IntegerLiteralRadix
{
    /// <summary>
    /// An ordinary base-10 literal, and the value reported for every literal that is not an
    /// integer at all. It is the default so that a consumer reading <see cref="LiteralExpression.Radix"/>
    /// never has to first establish what kind of literal it is holding.
    /// </summary>
    Decimal = 0,

    /// <summary>Base 2, spelled with an <c>0b</c> prefix. PostgreSQL 16 or later.</summary>
    Binary,

    /// <summary>Base 8, spelled with an <c>0o</c> prefix. PostgreSQL 16 or later.</summary>
    Octal,

    /// <summary>Base 16, spelled with an <c>0x</c> prefix. PostgreSQL 16 or later.</summary>
    Hexadecimal,
}

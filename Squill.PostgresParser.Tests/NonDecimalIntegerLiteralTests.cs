using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// PostgreSQL 16 added non-decimal integer literals (<c>0x1f</c>, <c>0o17</c>, <c>0b101</c>).
/// The vendored grammar has <c>BinaryIntegral</c>/<c>OctalIntegral</c>/<c>HexadecimalIntegral</c>
/// tokens and <c>iconst</c> accepts all three, so they reach the visitor — but the visitor read
/// every <c>iconst</c> with a bare <c>long.Parse</c>, which understands only decimal. The result
/// was a raw <see cref="FormatException"/> escaping the parser: not a
/// <see cref="PostgresParseException"/>, so nothing upstream could turn it into a source-anchored
/// diagnostic, and the author saw a framework stack trace instead of a message about their SQL.
///
/// <para>
/// These parse to an ordinary <see cref="LiteralExpression"/> like any other integer. The source
/// spelling is what round-trips (<see cref="ExpressionSqlRenderer"/> emits <c>Text</c>), so
/// <c>0x19</c> deploys as <c>0x19</c> rather than being silently rewritten to <c>25</c> — the
/// same principle that keeps <c>DEFAULT CURRENT_TIMESTAMP</c> distinct from <c>DEFAULT now()</c>.
/// </para>
///
/// <para>
/// Note the grammar's <c>Digits</c> fragment is <c>[0-9]+</c>, so a hex literal with a letter
/// digit (<c>0x1f</c>) does not lex as one token and is a plain syntax error. That is a
/// limitation of the vendored grammar, not something to work around here; only the
/// decimal-digit spellings are covered.
/// </para>
/// </summary>
public class NonDecimalIntegerLiteralTests
{
    private static LiteralExpression ParseColumnDefault(string sql)
    {
        var result = new AntlrPostgresParser().Parse(sql);

        var statement = Assert.IsType<CreateTableStatement>(Assert.Single(result.Statements));
        var column = Assert.IsType<ColumnDefinition>(Assert.Single(statement.Elements));
        var @default = Assert.Single(column.Constraints.OfType<DefaultColumnConstraint>());

        return Assert.IsType<LiteralExpression>(@default.Expression);
    }

    [Theory]
    [InlineData("0x19", 0x19L, IntegerLiteralRadix.Hexadecimal)]
    [InlineData("0X19", 0x19L, IntegerLiteralRadix.Hexadecimal)]
    [InlineData("0o17", 15L, IntegerLiteralRadix.Octal)]
    [InlineData("0O17", 15L, IntegerLiteralRadix.Octal)]
    [InlineData("0b101", 5L, IntegerLiteralRadix.Binary)]
    [InlineData("0B101", 5L, IntegerLiteralRadix.Binary)]
    public void NonDecimalLiteral_ParsesToItsValue(
        string literal, long expected, IntegerLiteralRadix radix)
    {
        var parsed = ParseColumnDefault($"CREATE TABLE t (c integer DEFAULT {literal});");

        // The value is decoded so anything comparing literals numerically agrees that 0x19 and
        // 25 are the same number...
        Assert.Equal(expected, parsed.Value);

        // ...while the text keeps the spelling the author used, which is what gets rendered
        // back out. Rewriting it to decimal would be a change to the source that no one asked
        // for, and would re-diff against a database that stored the original.
        Assert.Equal(literal, parsed.Text);

        // The radix is recorded because it is the version gate: the spelling, not the value, is
        // what PostgreSQL 16 introduced. Recovering it by re-scanning Text downstream would mean
        // re-deciding what the parser already knew from the token type.
        Assert.Equal(radix, parsed.Radix);
    }

    [Fact]
    public void DecimalLiteral_IsUnaffected()
    {
        var parsed = ParseColumnDefault("CREATE TABLE t (c integer DEFAULT 25);");

        Assert.Equal(25L, parsed.Value);
        Assert.Equal("25", parsed.Text);
        Assert.Equal(IntegerLiteralRadix.Decimal, parsed.Radix);
    }

    /// <summary>
    /// Every non-integer literal — strings, booleans, NULL — reports the decimal default, so a
    /// consumer can read <c>Radix</c> without first checking what kind of literal it has.
    /// </summary>
    [Theory]
    [InlineData("'abc'")]
    [InlineData("TRUE")]
    [InlineData("NULL")]
    public void NonIntegerLiteral_ReportsDecimalRadix(string literal)
    {
        var parsed = ParseColumnDefault($"CREATE TABLE t (c text DEFAULT {literal});");

        Assert.Equal(IntegerLiteralRadix.Decimal, parsed.Radix);
    }

    /// <summary>
    /// A literal too large for the value type is rejected, not wrapped around.
    /// <c>Convert.ToInt64</c> reinterprets an out-of-range binary/octal/hex string as two's
    /// complement instead of throwing, so <c>0x9999999999999999</c> would come out as
    /// <c>-7378697629483820647</c> — a negative number the source never wrote, and not even the
    /// value PostgreSQL gives it. Measured on 16: PostgreSQL returns the positive
    /// <c>11068046444225730969</c>, promoting past bigint rather than wrapping.
    ///
    /// <para>
    /// Rejecting is the honest answer while the value is modeled as a <c>long</c>: the decimal
    /// path already refuses the same magnitude, and silently changing a literal's sign is the one
    /// thing a literal must never do.
    /// </para>
    /// </summary>
    [Theory]
    // Hex, which Convert.ToInt64 would have wrapped to -7378697629483820647.
    [InlineData("0x9999999999999999")]
    // 64 ones, which it would have wrapped to -1.
    [InlineData("0b1111111111111111111111111111111111111111111111111111111111111111")]
    // The same magnitude in decimal, which already threw — but as a raw OverflowException that
    // escaped the parser, so the author saw a stack trace instead of a message about their SQL.
    [InlineData("11068046444225730969")]
    public void IntegerLiteral_TooLargeForItsValueType_IsRejected(string literal)
    {
        var ex = Assert.Throws<PostgresParseException>(
            () => new AntlrPostgresParser().Parse($"CREATE TABLE t (c bigint DEFAULT {literal});"));

        Assert.Contains(literal, ex.Message);
    }

    /// <summary>
    /// A value that only just fits is still accepted, so the range check rejects what is out of
    /// range rather than anything merely large. Spelled in binary because the grammar's
    /// <c>Digits</c> fragment is <c>[0-9]+</c>: a hex literal near the top of the range would need
    /// letter digits and would not lex.
    /// </summary>
    [Fact]
    public void IntegerLiteral_AtTheTopOfItsRange_IsAccepted()
    {
        // 63 ones is long.MaxValue.
        var parsed = ParseColumnDefault(
            $"CREATE TABLE t (c bigint DEFAULT 0b{new string('1', 63)});");

        Assert.Equal(long.MaxValue, parsed.Value);
    }

    /// <summary>
    /// The grammar's integral tokens accept any decimal digits after the prefix, so <c>0b999</c>
    /// and <c>0o99</c> lex happily even though neither is a real literal. PostgreSQL rejects
    /// them, and so must the parser — quietly reinterpreting <c>0b999</c> as some other number
    /// would deploy a value the source never named.
    /// </summary>
    [Theory]
    [InlineData("0b999")]
    [InlineData("0o99")]
    public void NonDecimalLiteral_WithDigitsOutsideItsRadix_Throws(string literal)
    {
        var ex = Assert.Throws<PostgresParseException>(
            () => new AntlrPostgresParser().Parse($"CREATE TABLE t (c integer DEFAULT {literal});"));

        Assert.Contains(literal, ex.Message);
    }
}

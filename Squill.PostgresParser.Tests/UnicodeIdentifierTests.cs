using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// Unicode-escaped identifiers (<c>U&amp;"d\0061t"</c>), and the <c>UESCAPE</c> clause that
/// redeclares the escape character (issue #200 review).
///
/// PostgreSQL DECODES these before storing the name, unlike a string constant, which it stores
/// as written. Measured against postgres:latest: both <c>U&amp;"d\0061t"</c> and
/// <c>U&amp;"d!0061t" UESCAPE '!'</c> create a table named <c>dat</c>. So an identifier is not a
/// construct Squill can carry verbatim the way it carries a literal — the model would record a
/// name the engine never creates, and every deploy would re-diff it.
///
/// Until the escapes are decoded, the parser must REJECT what it cannot represent rather than
/// silently record the wrong name. A build that fails is recoverable; a DACPAC that names the
/// wrong table is not.
/// </summary>
public class UnicodeIdentifierTests
{
    private static Root Parse(string sql) => new AntlrPostgresParser().Parse(sql);

    /// <summary>
    /// The regression this pins. The visitor deliberately reads only the quoted token so the
    /// trailing UESCAPE clause is excluded, and nothing else consumes it — so before the fix
    /// this parsed to a table named <c>d!0061t</c> where PostgreSQL would create <c>dat</c>.
    /// </summary>
    [Fact]
    public void UnicodeQuotedIdentifier_WithUescape_IsRejected()
    {
        var ex = Assert.ThrowsAny<Exception>(
            () => Parse("""CREATE TABLE U&"d!0061t" UESCAPE '!' (id int);"""));

        Assert.Contains("UESCAPE", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same for the default backslash escape, which needs no UESCAPE clause but is decoded
    /// just the same: <c>U&amp;"d\0061t"</c> is the table <c>dat</c>, not <c>d\0061t</c>.
    /// </summary>
    [Fact]
    public void UnicodeQuotedIdentifier_WithAnEscapeSequence_IsRejected()
    {
        Assert.ThrowsAny<Exception>(
            () => Parse("""CREATE TABLE U&"d\0061t" (id int);"""));
    }

    /// <summary>
    /// A unicode-quoted identifier containing NO escape sequence needs no decoding: the name is
    /// the literal text between the quotes, so it round-trips and must keep working.
    /// </summary>
    [Fact]
    public void UnicodeQuotedIdentifier_WithoutEscapes_ParsesToItsLiteralName()
    {
        var statement = Assert.IsType<CreateTableStatement>(
            Assert.Single(Parse("""CREATE TABLE U&"data" (id int);""").Statements));

        Assert.Equal("data", statement.Name.ToString());
    }

    /// <summary>
    /// A doubled escape character stands for one literal escape character, and PostgreSQL
    /// collapses it before storing the name. Measured against postgres:latest:
    /// <c>CREATE TABLE U&amp;"a\\b"</c> creates a table named <c>a\b</c>, three characters.
    ///
    /// So this is not an "escape sequence" to reject — it is representable — but it is also not
    /// the raw body, which would carry four characters and re-diff on every deploy against the
    /// three the server reports.
    /// </summary>
    [Fact]
    public void UnicodeQuotedIdentifier_WithADoubledEscape_CollapsesToOneEscapeCharacter()
    {
        var statement = Assert.IsType<CreateTableStatement>(
            Assert.Single(Parse("""CREATE TABLE U&"a\\b" (id int);""").Statements));

        Assert.Equal("""a\b""", statement.Name.ToString());
    }

    /// <summary>
    /// The same under a redeclared escape character, where the doubled form is the declared
    /// character rather than the backslash.
    /// </summary>
    [Fact]
    public void UnicodeQuotedIdentifier_WithADoubledCustomEscape_CollapsesToOneEscapeCharacter()
    {
        var statement = Assert.IsType<CreateTableStatement>(
            Assert.Single(Parse("""CREATE TABLE U&"a!!b" UESCAPE '!' (id int);""").Statements));

        Assert.Equal("a!b", statement.Name.ToString());
    }

    /// <summary>
    /// With the escape character redeclared, a BACKSLASH is just an ordinary character: it is
    /// no longer the escape, so it needs no collapsing and starts no sequence.
    /// </summary>
    [Fact]
    public void UnicodeQuotedIdentifier_WithACustomEscape_TreatsBackslashAsOrdinary()
    {
        var statement = Assert.IsType<CreateTableStatement>(
            Assert.Single(Parse("""CREATE TABLE U&"a\b" UESCAPE '!' (id int);""").Statements));

        Assert.Equal("""a\b""", statement.Name.ToString());
    }

    /// <summary>
    /// PostgreSQL accepts any string-constant spelling for the UESCAPE operand, not just the
    /// plain single-quoted form: <c>UESCAPE E'!'</c> and <c>UESCAPE $$!$$</c> both parse and
    /// both declare <c>!</c> as the escape (measured against postgres:latest, each creating a
    /// table named <c>dat</c>).
    ///
    /// Reading the operand's second character would take <c>'</c> from <c>E'!'</c> — the wrong
    /// escape character — and so mis-detect which sequences need rejecting. These must not be
    /// silently accepted with the wrong escape.
    /// </summary>
    [Theory]
    [InlineData("""CREATE TABLE U&"d!0061t" UESCAPE E'!' (id int);""")]
    [InlineData("""CREATE TABLE U&"d!0061t" UESCAPE $$!$$ (id int);""")]
    public void UnicodeQuotedIdentifier_WithANonPlainUescapeOperand_IsRejected(string sql)
    {
        Assert.ThrowsAny<Exception>(() => Parse(sql));
    }

    /// <summary>
    /// An ordinary quoted identifier is untouched by any of this.
    /// </summary>
    [Fact]
    public void QuotedIdentifier_ParsesToItsLiteralName()
    {
        var statement = Assert.IsType<CreateTableStatement>(
            Assert.Single(Parse("""CREATE TABLE "data" (id int);""").Statements));

        Assert.Equal("data", statement.Name.ToString());
    }
}

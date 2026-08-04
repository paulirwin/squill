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

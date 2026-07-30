using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Tests that an identifier longer than the 64-character limit both engines enforce is a
/// source-anchored build error (SQ0005) rather than a mid-deploy <c>ERROR 1059
/// (ER_TOO_LONG_IDENT)</c> from the server (issue #163).
///
/// <para>
/// The limit is measured in characters, not bytes: unlike PostgreSQL's 63-byte NAMEDATALEN,
/// MariaDB and MySQL both count characters, so a 64-character identifier of multi-byte
/// characters is accepted by the engines and must be accepted by the build too.
/// </para>
/// </summary>
public class IdentifierLengthTests
{
    // 65 characters: one over the limit. The tests below embed this in each identifier
    // position so the diagnostic is proven to fire for every identifier kind, not just the
    // index name that #163 was reported against.
    private const string TooLong =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // Exactly 64 characters: at the limit, which both engines accept.
    private const string AtLimit =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static ParserWorkspaceModelBuilder BuilderFor(params (string Name, string Sql)[] files)
    {
        var workspace = new Workspace();
        foreach (var (name, sql) in files)
        {
            workspace.Files.Add(new InMemoryStringFile(name, FileKind.Compile, sql));
        }

        return new ParserWorkspaceModelBuilder(
            workspace, new AntlrMariaDbParser(), new MariaDb12DatabaseSchemaProvider());
    }

    private static async Task<SqlSourceException> ErrorFor(string sql)
    {
        var builder = BuilderFor(("Test.sql", sql));

        return await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));
    }

    private static async Task NoErrorFor(string sql)
    {
        var builder = BuilderFor(("Test.sql", sql));

        // Building at all is the assertion: a length diagnostic would throw.
        await builder.ExtractModelAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void TestConstantsHaveTheLengthsTheyClaim()
    {
        Assert.Equal(65, TooLong.Length);
        Assert.Equal(64, AtLimit.Length);
    }

    [Fact]
    public async Task LongTableName_Errors()
    {
        var ex = await ErrorFor($"CREATE TABLE {TooLong} (id INT PRIMARY KEY);");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains("too long", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(TooLong, ex.Message);
        Assert.Equal(1, ex.Line);
    }

    [Fact]
    public async Task LongColumnName_Errors()
    {
        var ex = await ErrorFor($"CREATE TABLE book (id INT PRIMARY KEY, {TooLong} VARCHAR(50));");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains(TooLong, ex.Message);
    }

    [Fact]
    public async Task LongStandaloneIndexName_Errors()
    {
        var ex = await ErrorFor($"""
CREATE TABLE book (id INT PRIMARY KEY, title VARCHAR(50));
CREATE INDEX {TooLong} ON book (title);
""");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains(TooLong, ex.Message);
        Assert.Equal(2, ex.Line);
    }

    [Fact]
    public async Task LongInlineIndexName_Errors()
    {
        var ex = await ErrorFor($"""
CREATE TABLE book
(
    id INT PRIMARY KEY,
    title VARCHAR(50),
    KEY {TooLong} (title)
);
""");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains(TooLong, ex.Message);
    }

    [Fact]
    public async Task LongConstraintName_Errors()
    {
        var ex = await ErrorFor($"""
CREATE TABLE book
(
    id INT PRIMARY KEY,
    qty INT,
    CONSTRAINT {TooLong} CHECK (qty > 0)
);
""");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains(TooLong, ex.Message);
    }

    [Fact]
    public async Task LongForeignKeyName_Errors()
    {
        var ex = await ErrorFor($"""
CREATE TABLE author (id INT PRIMARY KEY);
CREATE TABLE book
(
    id INT PRIMARY KEY,
    author_id INT,
    CONSTRAINT {TooLong} FOREIGN KEY (author_id) REFERENCES author (id)
);
""");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains(TooLong, ex.Message);
    }

    [Fact]
    public async Task LongViewName_Errors()
    {
        var ex = await ErrorFor($"""
CREATE TABLE book (id INT PRIMARY KEY);
CREATE VIEW {TooLong} AS SELECT id FROM book;
""");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains(TooLong, ex.Message);
    }

    [Fact]
    public async Task LongProcedureName_Errors()
    {
        var ex = await ErrorFor($"CREATE PROCEDURE {TooLong}() BEGIN SELECT 1; END;");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains(TooLong, ex.Message);
    }

    [Fact]
    public async Task LongFunctionName_Errors()
    {
        var ex = await ErrorFor(
            $"CREATE FUNCTION {TooLong}() RETURNS INT DETERMINISTIC RETURN 1;");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains(TooLong, ex.Message);
    }

    [Fact]
    public async Task LongTriggerName_Errors()
    {
        var ex = await ErrorFor($"""
CREATE TABLE book (id INT PRIMARY KEY, qty INT);
CREATE TRIGGER {TooLong} BEFORE INSERT ON book FOR EACH ROW SET NEW.qty = 1;
""");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains(TooLong, ex.Message);
    }

    [Fact]
    public async Task LongEventName_Errors()
    {
        // An explicit STARTS is required of every event regardless of its name (the server
        // otherwise records the creation time, which can never round-trip), so it is given
        // one here to leave the length diagnostic as the only error.
        var ex = await ErrorFor($"""
CREATE EVENT {TooLong}
    ON SCHEDULE EVERY 1 DAY STARTS '2025-01-01 00:00:00'
    DO SELECT 1;
""");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains(TooLong, ex.Message);
    }

    /// <summary>
    /// A foreign key with no explicit name gets one derived from the table
    /// (<c>&lt;table&gt;_ibfk_&lt;n&gt;</c>). The derived name can exceed the limit even
    /// though every identifier the user wrote is within it, so the check has to run against
    /// the derived name rather than only against what is in the source text.
    /// </summary>
    [Fact]
    public async Task LongDerivedForeignKeyName_Errors()
    {
        // 60 characters, so "<table>_ibfk_1" is 60 + 7 = 67 — over the limit while the
        // table name itself is not.
        var table = new string('t', 60);

        var ex = await ErrorFor($"""
CREATE TABLE author (id INT PRIMARY KEY);
CREATE TABLE {table}
(
    id INT PRIMARY KEY,
    author_id INT,
    FOREIGN KEY (author_id) REFERENCES author (id)
);
""");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains("_ibfk_1", ex.Message);
    }

    [Fact]
    public async Task IdentifierAtTheLimit_IsAccepted()
    {
        await NoErrorFor($"CREATE TABLE {AtLimit} (id INT PRIMARY KEY);");
    }

    /// <summary>
    /// The limit is characters, not bytes. A 64-character identifier of 3-byte characters is
    /// 192 bytes and is accepted by both engines, so a byte-based check (which would be
    /// correct for PostgreSQL) would wrongly reject valid MariaDB source.
    /// </summary>
    [Fact]
    public async Task MultiByteIdentifierAtTheCharacterLimit_IsAccepted()
    {
        var name = new string('é', 64);
        Assert.Equal(64, name.Length);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) > 64);

        await NoErrorFor($"CREATE TABLE `{name}` (id INT PRIMARY KEY);");
    }

    /// <summary>
    /// Every over-long identifier is reported, not just the first — the validator accumulates
    /// so one build surfaces every problem, the way the duplicate-definition checks do.
    /// </summary>
    [Fact]
    public async Task MultipleLongIdentifiers_AreAllReported()
    {
        var builder = BuilderFor(("Test.sql", $"""
CREATE TABLE {TooLong}a (id INT PRIMARY KEY);
CREATE TABLE {TooLong}b (id INT PRIMARY KEY);
"""));

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.All(ex.InnerExceptions, i =>
            Assert.Equal(
                SqlSourceException.IdentifierTooLong,
                Assert.IsType<SqlSourceException>(i).Code));
    }
}

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

    /// <summary>
    /// A <em>column-level</em> foreign key derives an <c>_ibfk_N</c> name exactly as a
    /// table-level one does, so it must be checked too. Enumerating only table-level
    /// constraints skipped these entirely.
    /// </summary>
    [Fact]
    public async Task LongDerivedForeignKeyName_FromColumnLevelKey_Errors()
    {
        var table = new string('t', 60);

        var ex = await ErrorFor($"""
CREATE TABLE author (id INT PRIMARY KEY);
CREATE TABLE {table}
(
    id INT PRIMARY KEY,
    author_id INT REFERENCES author (id)
);
""");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains("_ibfk_1", ex.Message);
    }

    /// <summary>
    /// Column-level and table-level unnamed foreign keys share one <c>_ibfk_N</c> counter, and
    /// the column-level ones are numbered first. The ordinal reported must be the one that
    /// actually deploys, so a table with one of each numbers them 1 and 2 — not 1 and 1.
    /// </summary>
    [Fact]
    public async Task DerivedForeignKeyOrdinals_CountColumnLevelKeysFirst()
    {
        // 58 characters, so _ibfk_1 and _ibfk_2 are both 65 — one over the limit, giving two
        // errors whose ordinals pin the numbering.
        var table = new string('t', 58);

        var builder = BuilderFor(("Test.sql", $"""
CREATE TABLE author (id INT PRIMARY KEY);
CREATE TABLE {table}
(
    id INT PRIMARY KEY,
    author_id INT REFERENCES author (id),
    editor_id INT,
    FOREIGN KEY (editor_id) REFERENCES author (id)
);
"""));

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        var messages = ex.InnerExceptions.Select(i => i.Message).ToList();

        Assert.Contains(messages, m => m.Contains($"{table}_ibfk_1"));
        Assert.Contains(messages, m => m.Contains($"{table}_ibfk_2"));
    }

    /// <summary>
    /// A foreign key with an explicit CONSTRAINT name takes no ordinal, so an unnamed key
    /// declared after it is still <c>_ibfk_1</c>.
    /// </summary>
    [Fact]
    public async Task DerivedForeignKeyOrdinals_SkipExplicitlyNamedKeys()
    {
        var table = new string('t', 60);

        var ex = await ErrorFor($"""
CREATE TABLE author (id INT PRIMARY KEY);
CREATE TABLE {table}
(
    id INT PRIMARY KEY,
    author_id INT,
    editor_id INT,
    CONSTRAINT fk_author FOREIGN KEY (author_id) REFERENCES author (id),
    FOREIGN KEY (editor_id) REFERENCES author (id)
);
""");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains($"{table}_ibfk_1", ex.Message);
    }

    /// <summary>
    /// A routine's parameter names are part of its stored definition and are read back from
    /// the catalog, so an over-long one fails like the routine's own name.
    /// </summary>
    [Fact]
    public async Task LongProcedureParameterName_Errors()
    {
        var ex = await ErrorFor(
            $"CREATE PROCEDURE p(IN {TooLong} INT) BEGIN SELECT 1; END;");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains(TooLong, ex.Message);
    }

    [Fact]
    public async Task LongFunctionParameterName_Errors()
    {
        var ex = await ErrorFor(
            $"CREATE FUNCTION f({TooLong} INT) RETURNS INT DETERMINISTIC RETURN 1;");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains(TooLong, ex.Message);
    }

    /// <summary>
    /// A view's explicit column list names its columns outright. MySQL rejects an over-long
    /// one; MariaDB silently truncates it, which is worse — the extracted name would never
    /// match the declared one, so the view would re-diff on every deploy.
    /// </summary>
    [Fact]
    public async Task LongViewColumnName_Errors()
    {
        var ex = await ErrorFor($"""
CREATE TABLE book (id INT PRIMARY KEY);
CREATE VIEW v ({TooLong}) AS SELECT id FROM book;
""");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
        Assert.Contains(TooLong, ex.Message);
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
    /// A backtick-quoted identifier is measured by its unquoted content: the quotes are not
    /// part of the name the server stores, so counting them would reject a legal 64-character
    /// name. A quoted name may also contain a dot, which must not be mistaken for a qualifier
    /// and split — the whole thing is one identifier and is measured as one.
    /// </summary>
    [Fact]
    public async Task QuotedIdentifier_IsMeasuredUnquoted()
    {
        // 64 characters of content inside the quotes: legal, despite the quoted spelling
        // being 66 characters long.
        await NoErrorFor($"CREATE TABLE `{AtLimit}` (id INT PRIMARY KEY);");

        // A dot inside the quotes is part of the name, not a qualifier: 65 characters of
        // content is over the limit even though neither dot-separated part would be.
        var dotted = new string('a', 32) + "." + new string('a', 32);
        Assert.Equal(65, dotted.Length);

        var ex = await ErrorFor($"CREATE TABLE `{dotted}` (id INT PRIMARY KEY);");

        Assert.Equal(SqlSourceException.IdentifierTooLong, ex.Code);
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

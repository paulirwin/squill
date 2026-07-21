using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Tests that an object defined twice in the project is a build error (SQ0003), reported at
/// the second definition and naming where the first one was (issue #61).
/// </summary>
public class DuplicateDefinitionTests
{
    private static ParserWorkspaceModelBuilder BuilderFor(params (string Name, string Sql)[] files)
    {
        var workspace = new Workspace();
        foreach (var (name, sql) in files)
        {
            workspace.Files.Add(new InMemoryStringFile(name, FileKind.Compile, sql));
        }

        return new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser());
    }

    [Fact]
    public async Task DuplicateTable_InSameFile_Errors()
    {
        const string sql = """
CREATE TABLE book (id INT PRIMARY KEY);
CREATE TABLE book (id INT PRIMARY KEY);
""";
        var builder = BuilderFor(("Book.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0003", ex.Code);
        Assert.Equal(2, ex.Line);
        Assert.Contains("book", ex.Message);
    }

    [Fact]
    public async Task DuplicateTable_AcrossFiles_NamesFirstDefinition()
    {
        var builder = BuilderFor(
            ("A.sql", "CREATE TABLE book (id INT PRIMARY KEY);"),
            ("B.sql", "CREATE TABLE book (id INT PRIMARY KEY);"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0003", ex.Code);
        Assert.Equal("B.sql", ex.SourceFile);
        Assert.Contains("A.sql", ex.Message);
    }

    [Fact]
    public async Task DuplicateColumn_InSameTable_Errors()
    {
        const string sql = """
CREATE TABLE book
(
    id INT PRIMARY KEY,
    title VARCHAR(100),
    title VARCHAR(200)
);
""";
        var builder = BuilderFor(("Book.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0003", ex.Code);
        Assert.Contains("title", ex.Message);
    }

    [Fact]
    public async Task DuplicateIndexName_OnSameTable_Errors()
    {
        var builder = BuilderFor(
            ("Book.sql", "CREATE TABLE book (id INT PRIMARY KEY, title VARCHAR(50), isbn VARCHAR(20));"),
            ("A.sql", "CREATE INDEX ix_book ON book (title);"),
            ("B.sql", "CREATE INDEX ix_book ON book (isbn);"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0003", ex.Code);
        Assert.Contains("ix_book", ex.Message);
    }

    [Fact]
    public async Task InlineKeyAndCreateIndex_SharingAName_Errors()
    {
        // An inline KEY and a standalone CREATE INDEX share the table's index-name
        // namespace, so the collision has to be caught even though they are declared
        // through different syntax.
        var builder = BuilderFor(
            ("Book.sql", """
CREATE TABLE book
(
    id INT PRIMARY KEY,
    title VARCHAR(50),
    isbn VARCHAR(20),
    KEY ix_book (title)
);
"""),
            ("Index.sql", "CREATE INDEX ix_book ON book (isbn);"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0003", ex.Code);
        Assert.Contains("ix_book", ex.Message);
    }

    [Fact]
    public async Task SameIndexName_OnDifferentTables_Builds()
    {
        // Unlike Postgres, an index name only has to be unique within its table.
        var builder = BuilderFor(
            ("Book.sql", "CREATE TABLE book (id INT PRIMARY KEY, title VARCHAR(50));"),
            ("Author.sql", "CREATE TABLE author (id INT PRIMARY KEY, name VARCHAR(50));"),
            ("A.sql", "CREATE INDEX ix_name ON book (title);"),
            ("B.sql", "CREATE INDEX ix_name ON author (name);"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Model.Elements.Count(e => e.Type == MariaDbElementTypes.SqlIndex));
    }

    [Fact]
    public async Task DuplicateProcedure_Errors()
    {
        // MariaDB has no routine overloading: the name alone identifies the procedure.
        var builder = BuilderFor(
            ("A.sql", "CREATE PROCEDURE p(IN a INT) SELECT a;"),
            ("B.sql", "CREATE PROCEDURE p(IN a VARCHAR(10)) SELECT a;"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0003", ex.Code);
        Assert.Contains("p", ex.Message);
    }
}

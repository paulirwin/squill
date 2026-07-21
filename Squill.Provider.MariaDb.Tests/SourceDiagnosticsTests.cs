using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Tests that build failures carry source diagnostics: the file, line, and column of the
/// offending SQL (issue #53) — for both syntax errors and unresolved foreign key references.
/// </summary>
public class SourceDiagnosticsTests
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
    public void Parse_InvalidSql_ThrowsWithPosition()
    {
        var parser = new AntlrMariaDbParser();

        var ex = Assert.Throws<MariaDbParseException>(() => parser.Parse("this is not valid sql;"));

        Assert.Equal(1, ex.Line);
        Assert.NotNull(ex.Column);
    }

    [Fact]
    public async Task SyntaxError_CarriesFileAndPosition()
    {
        const string sql = """
CREATE TABLE foo (id int PRIMARY KEY);
CREATE bogus;
""";
        var builder = BuilderFor(("Bad.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Bad.sql", ex.SourceFile);
        Assert.Equal(2, ex.Line);
    }

    [Fact]
    public async Task ForeignKey_ToUndeclaredTable_Errors()
    {
        const string sql = """
CREATE TABLE book
(
    id int PRIMARY KEY,
    author_id int,
    CONSTRAINT fk_book_author FOREIGN KEY (author_id) REFERENCES author (id)
);
""";
        var builder = BuilderFor(("Book.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Book.sql", ex.SourceFile);
        Assert.Equal(5, ex.Line);
        Assert.Contains("author", ex.Message);
        Assert.Equal("SQ0002", ex.Code);
    }

    [Fact]
    public async Task ForeignKey_ToUndeclaredColumn_Errors()
    {
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE author (id int PRIMARY KEY);"),
            ("Book.sql", """
CREATE TABLE book
(
    id int PRIMARY KEY,
    author_id int,
    FOREIGN KEY (author_id) REFERENCES author (author_uuid)
);
"""));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Book.sql", ex.SourceFile);
        Assert.Contains("author_uuid", ex.Message);
    }

    [Fact]
    public async Task ForeignKey_AcrossFiles_Builds()
    {
        var builder = BuilderFor(
            ("Book.sql", """
CREATE TABLE book
(
    id int PRIMARY KEY,
    author_id int,
    FOREIGN KEY (author_id) REFERENCES author (id)
);
"""),
            ("Author.sql", "CREATE TABLE author (id int PRIMARY KEY);"));

        var model = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(model.Elements, e => e.Type == MariaDbElementTypes.SqlForeignKeyConstraint);
    }
}

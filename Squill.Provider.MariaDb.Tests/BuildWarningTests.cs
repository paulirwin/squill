using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Tests the non-fatal diagnostics channel (issue #61): constructs the parser recognizes but
/// does not model do not fail the build, but are reported as SQ1002 warnings so the gap is
/// visible rather than the construct silently vanishing from the DACPAC.
/// </summary>
public class BuildWarningTests
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
    public async Task CreateView_WarnsAndStillBuilds()
    {
        var builder = BuilderFor(
            ("Book.sql", "CREATE TABLE book (id INT PRIMARY KEY, title VARCHAR(50));"),
            ("View.sql", "CREATE VIEW v_book AS SELECT id FROM book;"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        // The table still builds; only the view is missing.
        Assert.Contains(result.Model.Elements, e => e.Type == MariaDbElementTypes.SqlTable);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Equal("View.sql", warning.SourceFile);
        Assert.Contains("CREATE VIEW", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FunctionDefault_Warns()
    {
        const string sql = """
CREATE TABLE event
(
    id INT PRIMARY KEY,
    created_at DATETIME DEFAULT CURRENT_TIMESTAMP
);
""";
        var builder = BuilderFor(("Event.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("created_at", warning.Message);
    }

    [Fact]
    public async Task ConstantDefault_DoesNotWarn()
    {
        const string sql = """
CREATE TABLE event
(
    id INT PRIMARY KEY,
    quantity INT DEFAULT 0
);
""";
        var builder = BuilderFor(("Event.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task CheckConstraint_Warns()
    {
        const string sql = """
CREATE TABLE product
(
    id INT PRIMARY KEY,
    price INT,
    CONSTRAINT ck_price CHECK (price > 0)
);
""";
        var builder = BuilderFor(("Product.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
    }

    [Fact]
    public async Task ColumnComment_Warns()
    {
        const string sql = """
CREATE TABLE book
(
    id INT PRIMARY KEY,
    title VARCHAR(50) COMMENT 'the title'
);
""";
        var builder = BuilderFor(("Book.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("title", warning.Message);
    }

    [Fact]
    public async Task CleanSource_ProducesNoWarnings()
    {
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE author (id INT PRIMARY KEY, name VARCHAR(50) NOT NULL);"),
            ("Book.sql", "CREATE TABLE book (id INT PRIMARY KEY, author_id INT, FOREIGN KEY (author_id) REFERENCES author (id));"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }
}

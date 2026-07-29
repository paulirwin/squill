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
        => BuilderFor(new MariaDb12DatabaseSchemaProvider(), files);

    private static ParserWorkspaceModelBuilder BuilderFor(
        MariaDbFamilyDatabaseSchemaProvider engine, params (string Name, string Sql)[] files)
    {
        var workspace = new Workspace();
        foreach (var (name, sql) in files)
        {
            workspace.Files.Add(new InMemoryStringFile(name, FileKind.Compile, sql));
        }

        return new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser(), engine);
    }

    /// <summary>
    /// A FULLTEXT / SPATIAL index is modeled as of issue #146, so it no longer warns. This was
    /// the last unmodeled construct in the Sakila sample — its <c>film_text</c> table declares
    /// <c>FULLTEXT KEY idx_title_description (title, description)</c> — so the sample now builds
    /// with zero SQ1002 warnings.
    /// </summary>
    [Theory]
    [InlineData("FULLTEXT KEY idx_t (title)")]
    [InlineData("FULLTEXT INDEX idx_t (title)")]
    [InlineData("SPATIAL KEY idx_t (g)")]
    [InlineData("SPATIAL INDEX idx_t (g)")]
    public async Task SpecialIndex_IsModeledWithoutWarning(string indexClause)
    {
        var builder = BuilderFor(("FilmText.sql", $"""
CREATE TABLE film_text
(
    film_id int NOT NULL PRIMARY KEY,
    title   varchar(255) NOT NULL,
    g       geometry NOT NULL,
    {indexClause}
);
"""));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
        Assert.Contains(result.Model.Elements, e => e.Type == MariaDbElementTypes.SqlIndex);
    }

    [Fact]
    public async Task CreateEvent_IsModeledWithoutWarning()
    {
        var builder = BuilderFor(
            ("Stats.sql", "CREATE TABLE stats (id INT PRIMARY KEY, n INT);"),
            ("Event.sql",
                "CREATE EVENT rollup ON SCHEDULE EVERY 1 DAY STARTS '2030-01-01 00:00:00' "
                + "DO UPDATE stats SET n = n + 1;"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        // A scheduled event is a modeled object as of issue #122, so it reaches the DACPAC
        // and no longer warns that it will be dropped from the build.
        Assert.Contains(result.Model.Elements, e => e.Type == MariaDbElementTypes.SqlEvent);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task CreateView_IsModeledWithoutWarning()
    {
        var builder = BuilderFor(
            ("Book.sql", "CREATE TABLE book (id INT PRIMARY KEY, title VARCHAR(50));"),
            ("View.sql", "CREATE VIEW v_book AS SELECT id FROM book;"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        // A view is a modeled object as of issue #42, so it reaches the DACPAC and no
        // longer warns that it will be dropped from the build.
        Assert.Contains(result.Model.Elements, e => e.Type == MariaDbElementTypes.SqlTable);
        Assert.Contains(result.Model.Elements, e => e.Type == MariaDbElementTypes.SqlView);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task FunctionDefault_Warns()
    {
        // CURRENT_TIMESTAMP is modeled as of issue #124, and its fractional-seconds form as of
        // issue #144, but an arbitrary function default is still outside the allowlist: it is
        // not a constant, so it cannot be trusted to round-trip.
        const string sql = """
CREATE TABLE event
(
    id INT PRIMARY KEY,
    created_at CHAR(36) DEFAULT (UUID())
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

    /// <summary>
    /// A named CHECK constraint is modeled as of issue #120, so it no longer warns as an
    /// unmodeled construct — it is carried into the model and deployed.
    /// </summary>
    [Fact]
    public async Task NamedCheckConstraint_DoesNotWarn()
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

        Assert.Empty(result.Warnings);
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

    /// <summary>
    /// The time-function defaults are modeled on MariaDB as of issue #147, so they no longer
    /// warn there — each gets its own canonical token rather than being dropped.
    /// </summary>
    [Theory]
    [InlineData("datetime DEFAULT LOCALTIME")]
    [InlineData("datetime DEFAULT LOCALTIMESTAMP")]
    [InlineData("date DEFAULT CURDATE()")]
    [InlineData("time DEFAULT CURTIME()")]
    public async Task OnMariaDb_TimeFunctionDefault_DoesNotWarn(string columnSql)
    {
        var builder = BuilderFor(
            new MariaDb12DatabaseSchemaProvider(), ("T.sql", $"CREATE TABLE t (id INT PRIMARY KEY, c {columnSql});"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// The same source targeting MySQL, where <c>CURDATE()</c>/<c>CURTIME()</c> are not valid
    /// defaults at all (measured: a syntax error), warns rather than silently deploying a
    /// script the server would reject. The message names the engine, since the identical source
    /// builds cleanly for MariaDB.
    /// </summary>
    [Theory]
    [InlineData("date DEFAULT CURDATE()")]
    [InlineData("time DEFAULT CURTIME()")]
    public async Task OnMySql_UnsupportedTimeFunctionDefault_Warns(string columnSql)
    {
        var builder = BuilderFor(
            new MySql9DatabaseSchemaProvider(), ("T.sql", $"CREATE TABLE t (id INT PRIMARY KEY, c {columnSql});"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        // Names the engine, so the reader is not sent looking for a malformed literal.
        Assert.Contains("MySql", warning.Message);
    }

    /// <summary>
    /// LOCALTIME/LOCALTIMESTAMP are true CURRENT_TIMESTAMP synonyms on MySQL, so they are
    /// modeled there and must not warn.
    /// </summary>
    [Theory]
    [InlineData("datetime DEFAULT LOCALTIME")]
    [InlineData("datetime DEFAULT LOCALTIMESTAMP")]
    public async Task OnMySql_LocaltimeFamily_DoesNotWarn(string columnSql)
    {
        var builder = BuilderFor(
            new MySql9DatabaseSchemaProvider(), ("T.sql", $"CREATE TABLE t (id INT PRIMARY KEY, c {columnSql});"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }
}

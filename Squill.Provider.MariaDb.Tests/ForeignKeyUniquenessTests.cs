using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Tests that a foreign key whose referenced columns are not backed by a primary key or
/// unique constraint/index is a build error (issue #61).
///
/// MariaDB and MySQL genuinely differ here: MariaDB accepts a foreign key backed by the
/// leftmost prefix of any index (unique or not), while MySQL 8+ requires a unique key on
/// exactly the referenced columns. Since one provider serves both engines, the stricter
/// MySQL rule is enforced — a DACPAC that builds is then deployable on either, whereas
/// allowing MariaDB's looser form would let a project build and fail on deploy to MySQL.
/// </summary>
public class ForeignKeyUniquenessTests
{
    private static ParserWorkspaceModelBuilder BuilderFor(params (string Name, string Sql)[] files)
    {
        var workspace = new Workspace();
        foreach (var (name, sql) in files)
        {
            workspace.Files.Add(new InMemoryStringFile(name, FileKind.Compile, sql));
        }

        return new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser(), MariaDbEngine.MariaDb);
    }

    [Fact]
    public async Task ForeignKey_ToNonUniqueColumn_Errors()
    {
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE author (id INT PRIMARY KEY, code VARCHAR(10));"),
            ("Book.sql", """
CREATE TABLE book
(
    id INT PRIMARY KEY,
    author_code VARCHAR(10),
    FOREIGN KEY (author_code) REFERENCES author (code)
);
"""));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0004", ex.Code);
        Assert.Equal("Book.sql", ex.SourceFile);
        Assert.Contains("code", ex.Message);
    }

    [Fact]
    public async Task ForeignKey_ToPrimaryKey_Builds()
    {
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE author (id INT PRIMARY KEY);"),
            ("Book.sql", """
CREATE TABLE book
(
    id INT PRIMARY KEY,
    author_id INT,
    FOREIGN KEY (author_id) REFERENCES author (id)
);
"""));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result.Model.Elements, e => e.Type == MariaDbElementTypes.SqlForeignKeyConstraint);
    }

    [Fact]
    public async Task ForeignKey_ToUniqueConstraintColumn_Builds()
    {
        var builder = BuilderFor(
            ("Author.sql", """
CREATE TABLE author
(
    id INT PRIMARY KEY,
    code VARCHAR(10),
    UNIQUE KEY uq_author_code (code)
);
"""),
            ("Book.sql", """
CREATE TABLE book
(
    id INT PRIMARY KEY,
    author_code VARCHAR(10),
    FOREIGN KEY (author_code) REFERENCES author (code)
);
"""));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result.Model.Elements, e => e.Type == MariaDbElementTypes.SqlForeignKeyConstraint);
    }

    [Fact]
    public async Task ForeignKey_ToInlineUniqueColumn_Builds()
    {
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE author (id INT PRIMARY KEY, code VARCHAR(10) UNIQUE);"),
            ("Book.sql", """
CREATE TABLE book
(
    id INT PRIMARY KEY,
    author_code VARCHAR(10),
    FOREIGN KEY (author_code) REFERENCES author (code)
);
"""));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result.Model.Elements, e => e.Type == MariaDbElementTypes.SqlForeignKeyConstraint);
    }

    [Fact]
    public async Task ForeignKey_ToUniqueIndexColumn_Builds()
    {
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE author (id INT PRIMARY KEY, code VARCHAR(10));"),
            ("Index.sql", "CREATE UNIQUE INDEX ux_author_code ON author (code);"),
            ("Book.sql", """
CREATE TABLE book
(
    id INT PRIMARY KEY,
    author_code VARCHAR(10),
    FOREIGN KEY (author_code) REFERENCES author (code)
);
"""));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result.Model.Elements, e => e.Type == MariaDbElementTypes.SqlForeignKeyConstraint);
    }

    [Fact]
    public async Task ForeignKey_NoColumnList_ToTableWithoutPrimaryKey_Errors()
    {
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE author (id INT NOT NULL);"),
            ("Book.sql", """
CREATE TABLE book
(
    id INT PRIMARY KEY,
    author_id INT,
    FOREIGN KEY (author_id) REFERENCES author
);
"""));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0004", ex.Code);
        Assert.Contains("primary key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForeignKey_ToPartOfCompositePrimaryKey_Errors()
    {
        // MariaDB would accept this (a leftmost prefix of the PK), but MySQL 8+ rejects it
        // with "Missing unique key for constraint ... in the referenced table". One provider
        // serves both engines, so the stricter MySQL rule is enforced — otherwise a project
        // would build and then fail to deploy against MySQL.
        const string sql = """
CREATE TABLE orders
(
    id INT NOT NULL,
    line_no INT NOT NULL,
    PRIMARY KEY (id, line_no)
);
CREATE TABLE notes
(
    id INT PRIMARY KEY,
    order_id INT,
    FOREIGN KEY (order_id) REFERENCES orders (id)
);
""";
        var builder = BuilderFor(("Orders.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0004", ex.Code);
    }

    [Fact]
    public async Task ForeignKey_ToPlainIndexedColumn_Errors()
    {
        // Likewise: a non-unique KEY satisfies MariaDB but not MySQL.
        var builder = BuilderFor(
            ("Author.sql", """
CREATE TABLE author
(
    id INT PRIMARY KEY,
    code VARCHAR(10),
    KEY ix_author_code (code)
);
"""),
            ("Book.sql", """
CREATE TABLE book
(
    id INT PRIMARY KEY,
    author_code VARCHAR(10),
    FOREIGN KEY (author_code) REFERENCES author (code)
);
"""));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0004", ex.Code);
    }

    [Fact]
    public async Task ForeignKey_Composite_MatchingCompositePrimaryKey_Builds()
    {
        const string sql = """
CREATE TABLE orders
(
    id INT NOT NULL,
    line_no INT NOT NULL,
    PRIMARY KEY (id, line_no)
);
CREATE TABLE shipments
(
    order_id INT NOT NULL,
    line_no INT NOT NULL,
    FOREIGN KEY (order_id, line_no) REFERENCES orders (id, line_no)
);
""";
        var builder = BuilderFor(("Orders.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result.Model.Elements, e => e.Type == MariaDbElementTypes.SqlForeignKeyConstraint);
    }
}

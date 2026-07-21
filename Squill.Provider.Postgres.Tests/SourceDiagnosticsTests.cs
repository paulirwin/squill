using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

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

        return new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser());
    }

    [Fact]
    public async Task SyntaxError_CarriesFileAndPosition()
    {
        const string sql = """
CREATE TABLE foo (id integer PRIMARY KEY);
CREATE bogus;
""";
        var builder = BuilderFor(("Bad.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Bad.sql", ex.SourceFile);
        Assert.Equal(2, ex.Line);
        Assert.NotNull(ex.Column);
    }

    [Fact]
    public async Task InlineForeignKey_ToUndeclaredTable_Errors()
    {
        const string sql = """
CREATE TABLE book
(
    id integer PRIMARY KEY,
    author_id integer REFERENCES author (id)
);
""";
        var builder = BuilderFor(("Book.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Book.sql", ex.SourceFile);
        Assert.Equal(4, ex.Line);
        Assert.Contains("author", ex.Message);
        Assert.Equal("SQ0002", ex.Code);
    }

    [Fact]
    public async Task TableLevelForeignKey_ToUndeclaredTable_ErrorsAtConstraintLine()
    {
        const string sql = """
CREATE TABLE book
(
    id integer PRIMARY KEY,
    author_id integer,
    CONSTRAINT fk_book_author FOREIGN KEY (author_id) REFERENCES author (id)
);
""";
        var builder = BuilderFor(("Book.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Book.sql", ex.SourceFile);
        Assert.Equal(5, ex.Line);
        Assert.Contains("author", ex.Message);
    }

    [Fact]
    public async Task ForeignKey_ToUndeclaredColumn_Errors()
    {
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE author (id integer PRIMARY KEY);"),
            ("Book.sql", "CREATE TABLE book (id integer PRIMARY KEY, author_id integer REFERENCES author (author_uuid));"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Book.sql", ex.SourceFile);
        Assert.Contains("author_uuid", ex.Message);
        Assert.Equal("SQ0002", ex.Code);
    }

    [Fact]
    public async Task ForeignKey_AcrossFiles_Builds()
    {
        var builder = BuilderFor(
            // Declared after its referencing file alphabetically — order must not matter.
            ("Book.sql", "CREATE TABLE book (id integer PRIMARY KEY, author_id integer REFERENCES author (id));"),
            ("Author.sql", "CREATE TABLE author (id integer PRIMARY KEY);"));

        var model = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(model.Elements, e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint);
    }

    [Fact]
    public async Task ForeignKey_SelfReference_Builds()
    {
        const string sql = """
CREATE TABLE employee
(
    id integer PRIMARY KEY,
    manager_id integer REFERENCES employee (id)
);
""";
        var builder = BuilderFor(("Employee.sql", sql));

        var model = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(model.Elements, e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint);
    }

    [Fact]
    public async Task ForeignKey_ToSchemaQualifiedDeclaredTable_Builds()
    {
        var builder = BuilderFor(
            ("Schema.sql", "CREATE SCHEMA staging;"),
            ("Author.sql", "CREATE TABLE staging.author (id integer PRIMARY KEY);"),
            ("Book.sql", "CREATE TABLE book (id integer PRIMARY KEY, author_id integer REFERENCES staging.author (id));"));

        var model = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(model.Elements, e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint);
    }

    [Fact]
    public async Task ForeignKey_WithoutReferencedColumns_ChecksTableOnly()
    {
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE author (id integer PRIMARY KEY);"),
            // No referenced column list: defaults to the referenced table's primary key.
            ("Book.sql", "CREATE TABLE book (id integer PRIMARY KEY, author_id integer REFERENCES author);"));

        var model = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(model.Elements, e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint);
    }

    [Fact]
    public async Task Index_OnUndeclaredTable_Errors()
    {
        var builder = BuilderFor(("Index.sql", "CREATE INDEX ix_missing ON nope (id);"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Index.sql", ex.SourceFile);
        Assert.Equal(1, ex.Line);
        Assert.Equal("SQ0002", ex.Code);
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public async Task Index_OnUndeclaredColumn_Errors()
    {
        var builder = BuilderFor(
            ("Foo.sql", "CREATE TABLE foo (id integer PRIMARY KEY);"),
            ("Index.sql", "CREATE INDEX ix_foo ON foo (nope);"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Index.sql", ex.SourceFile);
        Assert.Equal("SQ0002", ex.Code);
        Assert.Contains("foo.nope", ex.Message);
    }

    [Fact]
    public async Task Index_OnTableInAnotherFile_Builds()
    {
        var builder = BuilderFor(
            ("Index.sql", "CREATE INDEX ix_foo_name ON foo (name);"),
            ("Foo.sql", "CREATE TABLE foo (id integer PRIMARY KEY, name varchar(50) NOT NULL);"));

        var model = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(model.Elements, e => e.Type == PostgresElementTypes.SqlIndex);
    }

    [Fact]
    public async Task PrimaryKey_OnMissingColumn_Errors()
    {
        const string sql = """
CREATE TABLE orders
(
    id integer NOT NULL,
    PRIMARY KEY (id, nope)
);
""";
        var builder = BuilderFor(("Orders.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Orders.sql", ex.SourceFile);
        Assert.Equal(4, ex.Line);
        Assert.Equal("SQ0002", ex.Code);
        Assert.Contains("orders.nope", ex.Message);
    }

    [Fact]
    public async Task ForeignKey_LocalColumnMissing_Errors()
    {
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE author (id integer PRIMARY KEY);"),
            ("Book.sql", """
CREATE TABLE book
(
    id integer PRIMARY KEY,
    author_id integer,
    FOREIGN KEY (typo_id) REFERENCES author (id)
);
"""));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Book.sql", ex.SourceFile);
        Assert.Equal(5, ex.Line);
        Assert.Equal("SQ0002", ex.Code);
        Assert.Contains("book.typo_id", ex.Message);
    }

    [Fact]
    public async Task ForeignKey_ColumnCountMismatch_Errors()
    {
        const string sql = """
CREATE TABLE orders
(
    id integer NOT NULL,
    line_no integer NOT NULL,
    PRIMARY KEY (id, line_no)
);
CREATE TABLE order_lines
(
    order_id integer NOT NULL,
    line_no integer NOT NULL,
    FOREIGN KEY (order_id, line_no) REFERENCES orders (id)
);
""";
        var builder = BuilderFor(("Orders.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Orders.sql", ex.SourceFile);
        Assert.Equal(11, ex.Line);
        Assert.Equal("SQ0004", ex.Code);
    }

    [Fact]
    public async Task Table_InUndeclaredSchema_Errors()
    {
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE staging.author (id integer PRIMARY KEY);"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Author.sql", ex.SourceFile);
        Assert.Equal(1, ex.Line);
        Assert.Equal("SQ0002", ex.Code);
        Assert.Contains("staging", ex.Message);
    }

    [Fact]
    public async Task MultipleUnresolvedForeignKeys_ReportsAll()
    {
        var builder = BuilderFor(
            ("Book.sql", "CREATE TABLE book (id integer PRIMARY KEY, author_id integer REFERENCES author (id));"),
            ("Review.sql", "CREATE TABLE review (id integer PRIMARY KEY, reviewer_id integer REFERENCES reviewer (id));"));

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.All(ex.InnerExceptions, inner => Assert.IsType<SqlSourceException>(inner));
    }
}

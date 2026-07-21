using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Tests that a foreign key whose referenced columns are not backed by a primary key or
/// unique constraint is a build error (issue #61). Postgres requires this at deploy time
/// ("there is no unique constraint matching given keys for referenced table"); since the
/// PK/unique information is already collected at build time, it can be caught much earlier.
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

        return new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser());
    }

    [Fact]
    public async Task ForeignKey_ToNonUniqueColumn_Errors()
    {
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE author (id integer PRIMARY KEY, code varchar(10));"),
            ("Book.sql", "CREATE TABLE book (id integer PRIMARY KEY, author_code varchar(10) REFERENCES author (code));"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0004", ex.Code);
        Assert.Equal("Book.sql", ex.SourceFile);
        Assert.Contains("author", ex.Message);
        Assert.Contains("code", ex.Message);
    }

    [Fact]
    public async Task ForeignKey_ToPrimaryKey_Builds()
    {
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE author (id integer PRIMARY KEY);"),
            ("Book.sql", "CREATE TABLE book (id integer PRIMARY KEY, author_id integer REFERENCES author (id));"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint);
    }

    [Fact]
    public async Task ForeignKey_ToUniqueIndexColumn_Builds()
    {
        // A CREATE UNIQUE INDEX also satisfies Postgres's requirement.
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE author (id integer PRIMARY KEY, code varchar(10));"),
            ("Index.sql", "CREATE UNIQUE INDEX ux_author_code ON author (code);"),
            ("Book.sql", "CREATE TABLE book (id integer PRIMARY KEY, author_code varchar(10) REFERENCES author (code));"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint);
    }

    [Fact]
    public async Task ForeignKey_ToNonUniqueIndexColumn_Errors()
    {
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE author (id integer PRIMARY KEY, code varchar(10));"),
            ("Index.sql", "CREATE INDEX ix_author_code ON author (code);"),
            ("Book.sql", "CREATE TABLE book (id integer PRIMARY KEY, author_code varchar(10) REFERENCES author (code));"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0004", ex.Code);
    }

    [Fact]
    public async Task ForeignKey_NoColumnList_ToTableWithoutPrimaryKey_Errors()
    {
        // Omitting the column list means "the referenced table's primary key" — so a table
        // with no primary key cannot be the target at all.
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE author (id integer NOT NULL);"),
            ("Book.sql", "CREATE TABLE book (id integer PRIMARY KEY, author_id integer REFERENCES author);"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0004", ex.Code);
        Assert.Contains("author", ex.Message);
        Assert.Contains("primary key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ForeignKey_Composite_MatchingCompositePrimaryKey_Builds()
    {
        const string sql = """
CREATE TABLE orders
(
    id integer NOT NULL,
    line_no integer NOT NULL,
    PRIMARY KEY (id, line_no)
);
CREATE TABLE shipments
(
    order_id integer NOT NULL,
    line_no integer NOT NULL,
    FOREIGN KEY (order_id, line_no) REFERENCES orders (id, line_no)
);
""";
        var builder = BuilderFor(("Orders.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint);
    }

    [Fact]
    public async Task ForeignKey_ToPartOfCompositePrimaryKey_Errors()
    {
        // A composite PK on (id, line_no) does not make `id` alone unique.
        const string sql = """
CREATE TABLE orders
(
    id integer NOT NULL,
    line_no integer NOT NULL,
    PRIMARY KEY (id, line_no)
);
CREATE TABLE notes
(
    id integer PRIMARY KEY,
    order_id integer REFERENCES orders (id)
);
""";
        var builder = BuilderFor(("Orders.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0004", ex.Code);
    }

    [Fact]
    public async Task ForeignKey_ColumnOrderDiffersFromUniqueConstraint_Builds()
    {
        // Postgres matches the unique constraint as a set of columns, not an ordered list.
        const string sql = """
CREATE TABLE orders
(
    id integer NOT NULL,
    line_no integer NOT NULL,
    PRIMARY KEY (id, line_no)
);
CREATE TABLE shipments
(
    line_no integer NOT NULL,
    order_id integer NOT NULL,
    FOREIGN KEY (line_no, order_id) REFERENCES orders (line_no, id)
);
""";
        var builder = BuilderFor(("Orders.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint);
    }

    [Fact]
    public async Task ForeignKey_SelfReferenceToPrimaryKey_Builds()
    {
        const string sql = """
CREATE TABLE employee
(
    id integer PRIMARY KEY,
    manager_id integer REFERENCES employee (id)
);
""";
        var builder = BuilderFor(("Employee.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint);
    }

    [Fact]
    public async Task ForeignKey_ToUniqueIndexAcrossSchemas_Builds()
    {
        var builder = BuilderFor(
            ("Schema.sql", "CREATE SCHEMA staging;"),
            ("Author.sql", "CREATE TABLE staging.author (id integer PRIMARY KEY, code varchar(10));"),
            ("Index.sql", "CREATE UNIQUE INDEX ux_author_code ON staging.author (code);"),
            ("Book.sql", "CREATE TABLE book (id integer PRIMARY KEY, author_code varchar(10) REFERENCES staging.author (code));"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint);
    }
}

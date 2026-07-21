using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Tests that an object defined twice in the project is a build error (SQ0003), reported at
/// the second definition and naming where the first one was (issue #61). Without this the
/// validator's declared-table map silently last-wins and the model carries both elements,
/// which would confuse diffing.
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

        return new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser());
    }

    [Fact]
    public async Task DuplicateTable_InSameFile_Errors()
    {
        const string sql = """
CREATE TABLE book (id integer PRIMARY KEY);
CREATE TABLE book (id integer PRIMARY KEY);
""";
        var builder = BuilderFor(("Book.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0003", ex.Code);
        Assert.Equal("Book.sql", ex.SourceFile);
        // Reported at the second definition, not the first.
        Assert.Equal(2, ex.Line);
        Assert.Contains("book", ex.Message);
    }

    [Fact]
    public async Task DuplicateTable_AcrossFiles_NamesFirstDefinition()
    {
        var builder = BuilderFor(
            ("A.sql", "CREATE TABLE book (id integer PRIMARY KEY);"),
            ("B.sql", "CREATE TABLE book (id integer PRIMARY KEY);"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0003", ex.Code);
        Assert.Equal("B.sql", ex.SourceFile);
        // The message should point at where the first definition lives.
        Assert.Contains("A.sql", ex.Message);
    }

    [Fact]
    public async Task DuplicateTable_DiffersOnlyByCase_Errors()
    {
        // Postgres folds unquoted identifiers to lowercase, so these are the same table.
        var builder = BuilderFor(
            ("A.sql", "CREATE TABLE book (id integer PRIMARY KEY);"),
            ("B.sql", "CREATE TABLE BOOK (id integer PRIMARY KEY);"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0003", ex.Code);
    }

    [Fact]
    public async Task SameTableName_InDifferentSchemas_Builds()
    {
        var builder = BuilderFor(
            ("Schema.sql", "CREATE SCHEMA staging;"),
            ("A.sql", "CREATE TABLE book (id integer PRIMARY KEY);"),
            ("B.sql", "CREATE TABLE staging.book (id integer PRIMARY KEY);"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Model.Elements.Count(e => e.Type == PostgresElementTypes.SqlTable));
    }

    [Fact]
    public async Task DuplicateColumn_InSameTable_Errors()
    {
        const string sql = """
CREATE TABLE book
(
    id integer PRIMARY KEY,
    title varchar(100),
    title varchar(200)
);
""";
        var builder = BuilderFor(("Book.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0003", ex.Code);
        Assert.Contains("title", ex.Message);
    }

    [Fact]
    public async Task DuplicateConstraintName_InSameSchema_Errors()
    {
        var builder = BuilderFor(
            ("A.sql", """
CREATE TABLE book
(
    id integer,
    CONSTRAINT pk_shared PRIMARY KEY (id)
);
"""),
            ("B.sql", """
CREATE TABLE author
(
    id integer,
    CONSTRAINT pk_shared PRIMARY KEY (id)
);
"""));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0003", ex.Code);
        Assert.Contains("pk_shared", ex.Message);
    }

    [Fact]
    public async Task SameForeignKeyConstraintName_OnDifferentTables_Builds()
    {
        // Verified against real Postgres: a FOREIGN KEY constraint name is scoped to its
        // table, so two tables may share one. Only index-backed constraints (PK/UNIQUE) take
        // a name in the schema's relation namespace.
        var builder = BuilderFor(
            ("Parent.sql", "CREATE TABLE parent (id integer PRIMARY KEY);"),
            ("A.sql", """
CREATE TABLE a
(
    id integer PRIMARY KEY,
    pid integer,
    CONSTRAINT shared_fk FOREIGN KEY (pid) REFERENCES parent (id)
);
"""),
            ("B.sql", """
CREATE TABLE b
(
    id integer PRIMARY KEY,
    pid integer,
    CONSTRAINT shared_fk FOREIGN KEY (pid) REFERENCES parent (id)
);
"""));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Model.Elements.Count(
            e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint));
    }

    [Fact]
    public async Task DuplicateIndexName_Errors()
    {
        var builder = BuilderFor(
            ("Book.sql", "CREATE TABLE book (id integer PRIMARY KEY, title varchar(50), isbn varchar(20));"),
            ("A.sql", "CREATE INDEX ix_book ON book (title);"),
            ("B.sql", "CREATE INDEX ix_book ON book (isbn);"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0003", ex.Code);
        Assert.Equal("B.sql", ex.SourceFile);
        Assert.Contains("ix_book", ex.Message);
    }

    [Fact]
    public async Task DuplicateProcedure_WithSameSignature_Errors()
    {
        var builder = BuilderFor(
            ("A.sql", "CREATE PROCEDURE p(a integer) LANGUAGE sql AS $$ SELECT 1 $$;"),
            ("B.sql", "CREATE PROCEDURE p(a integer) LANGUAGE sql AS $$ SELECT 2 $$;"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("SQ0003", ex.Code);
    }

    [Fact]
    public async Task OverloadedProcedure_DifferentSignature_Builds()
    {
        // Postgres identifies a routine by name *and* argument types, so these coexist.
        var builder = BuilderFor(
            ("A.sql", "CREATE PROCEDURE p(a integer) LANGUAGE sql AS $$ SELECT 1 $$;"),
            ("B.sql", "CREATE PROCEDURE p(a text) LANGUAGE sql AS $$ SELECT 2 $$;"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Model.Elements.Count(e => e.Type == PostgresElementTypes.SqlProcedure));
    }
}

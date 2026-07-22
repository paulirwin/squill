using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Tests that a build reports errors from every file in one pass rather than failing fast on
/// the first one (issue #61). Previously a syntax error in the first file aborted the build,
/// so errors elsewhere only surfaced on the next rebuild — one round-trip per broken file.
/// </summary>
public class MultiFileErrorReportingTests
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
    public async Task SyntaxErrors_InMultipleFiles_AllReported()
    {
        var builder = BuilderFor(
            ("A.sql", "CREATE bogus;"),
            ("B.sql", "CREATE alsobogus;"));

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        var errors = ex.InnerExceptions.Cast<SqlSourceException>().ToList();

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.SourceFile == "A.sql");
        Assert.Contains(errors, e => e.SourceFile == "B.sql");
        Assert.All(errors, e => Assert.Equal("SQ0001", e.Code));
    }

    [Fact]
    public async Task SyntaxError_InOneFile_StillReportsReferenceErrorsInOthers()
    {
        // A broken file must not mask a genuine reference error elsewhere.
        var builder = BuilderFor(
            ("Broken.sql", "CREATE bogus;"),
            ("Book.sql", "CREATE TABLE book (id integer PRIMARY KEY, author_id integer REFERENCES author (id));"));

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        var errors = ex.InnerExceptions.Cast<SqlSourceException>().ToList();

        Assert.Contains(errors, e => e.SourceFile == "Broken.sql" && e.Code == "SQ0001");
        Assert.Contains(errors, e => e.SourceFile == "Book.sql" && e.Code == "SQ0002");
    }

    [Fact]
    public async Task SingleSyntaxError_StillThrowsSqlSourceExceptionDirectly()
    {
        // One error stays a bare SqlSourceException so existing hosts and tests are unaffected.
        var builder = BuilderFor(("A.sql", "CREATE bogus;"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("A.sql", ex.SourceFile);
    }

    [Fact]
    public async Task MappingErrors_InMultipleStatements_AllReported()
    {
        // Unsupported constructs that parse but fail during model mapping: a function
        // parameter DEFAULT is not modeled, so each is a mapping error, and first-one-wins
        // would report only one of these.
        var builder = BuilderFor(
            ("A.sql", "CREATE FUNCTION f(a integer DEFAULT 1) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;"),
            ("B.sql", "CREATE FUNCTION g(a integer DEFAULT 1) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;"));

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, ex.InnerExceptions.Count);
        Assert.All(ex.InnerExceptions, inner => Assert.IsType<SqlSourceException>(inner));
    }

    [Fact]
    public async Task MappingErrors_InSameFile_BothReported()
    {
        const string sql = """
CREATE FUNCTION f(a integer DEFAULT 1) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;
CREATE FUNCTION g(a integer DEFAULT 1) RETURNS integer LANGUAGE sql AS $$ SELECT a $$;
""";
        var builder = BuilderFor(("A.sql", sql));

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        var errors = ex.InnerExceptions.Cast<SqlSourceException>().ToList();

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Line == 1);
        Assert.Contains(errors, e => e.Line == 2);
    }
}

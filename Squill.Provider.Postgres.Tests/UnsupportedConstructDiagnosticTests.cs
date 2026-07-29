using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// A construct the parser recognizes but cannot map throws <see cref="NotImplementedException"/>
/// from inside <c>IPostgresParser.Parse</c>, before any statement exists to anchor to. Those
/// used to escape <c>ProcessFile</c>, which caught only <see cref="PostgresParseException"/> on
/// the parse call — so the user got a raw stack trace instead of a source-anchored SQ0001
/// pointing at the offending file (issue #159, secondary defect).
/// </summary>
public class UnsupportedConstructDiagnosticTests
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

    /// <summary>
    /// A base type (CREATE TYPE name (INPUT = ..., OUTPUT = ...)) parses but is not modelable,
    /// so VisitDefinestmt throws NotImplementedException from within Parse.
    /// </summary>
    [Fact]
    public async Task UnsupportedConstruct_IsReportedAsASourceAnchoredError()
    {
        var builder = BuilderFor(
            ("Types.sql", "CREATE TYPE mytype (INPUT = myin, OUTPUT = myout);"));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Types.sql", ex.SourceFile);
        Assert.Equal(SqlSourceException.SyntaxError, ex.Code);

        // The original failure is kept as the inner exception so the reason survives.
        Assert.IsType<NotImplementedException>(ex.InnerException);
    }

    /// <summary>
    /// And it must not abort the whole build: an unsupported construct in one file still lets
    /// the other files report their own errors in the same pass (issue #61's guarantee).
    /// </summary>
    [Fact]
    public async Task UnsupportedConstruct_DoesNotMaskErrorsInOtherFiles()
    {
        var builder = BuilderFor(
            ("Types.sql", "CREATE TYPE mytype (INPUT = myin, OUTPUT = myout);"),
            ("Bogus.sql", "CREATE bogus;"));

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        var errors = ex.InnerExceptions.Cast<SqlSourceException>().ToList();

        Assert.Contains(errors, e => e.SourceFile == "Types.sql");
        Assert.Contains(errors, e => e.SourceFile == "Bogus.sql");
        Assert.All(errors, e => Assert.Equal(SqlSourceException.SyntaxError, e.Code));
    }
}

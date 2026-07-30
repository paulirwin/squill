using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Squill is declarative: source files state the desired end-state as CREATE, and the diff
/// engine generates the ALTER/DROP. An authored ALTER, DROP or DML statement in a compiled
/// source file is therefore a user error, and gets its own SQ0006 diagnostic saying so
/// (issue #125).
///
/// <para>
/// Before this, such a statement surfaced as SQ0001 carrying the internal message
/// "Expected VisitStmt to return a Statement" with no line or column at all — wording that
/// reads as a Squill limitation ("not yet implemented", "unresolved reference") rather than
/// as the deliberate rejection it is.
/// </para>
/// </summary>
public class ImperativeStatementDiagnosticTests
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

    [Theory]
    [InlineData("ALTER TABLE t ADD COLUMN c integer;", "ALTER TABLE")]
    [InlineData("ALTER TABLE t DROP COLUMN c;", "ALTER TABLE")]
    [InlineData("ALTER INDEX ix RENAME TO ix2;", "ALTER INDEX")]
    [InlineData("DROP TABLE t;", "DROP TABLE")]
    [InlineData("DROP INDEX ix;", "DROP INDEX")]
    [InlineData("DROP SCHEMA s;", "DROP SCHEMA")]
    [InlineData("TRUNCATE TABLE t;", "TRUNCATE")]
    public async Task SchemaChangingStatement_IsRejectedWithSq0006(string sql, string expectedName)
    {
        var builder = BuilderFor(("A.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SqlSourceException.ImperativeStatement, ex.Code);
        Assert.Equal("A.sql", ex.SourceFile);

        // The statement it is about, and the remedy — not a "not yet implemented" that reads
        // as a missing capability.
        Assert.Contains(expectedName, ex.Message);
        Assert.Contains("declarative", ex.Message);
        Assert.Contains("CREATE", ex.Message);
        Assert.DoesNotContain("not yet implemented", ex.Message);
        Assert.DoesNotContain("VisitStmt", ex.Message);
    }

    /// <summary>
    /// DML gets the same code but different wording: seed and reference data is a legitimate
    /// thing to want, and Squill already supports it through pre/post-deploy scripts, so the
    /// message points there rather than at CREATE.
    /// </summary>
    [Theory]
    [InlineData("INSERT INTO t (c) VALUES (1);", "INSERT")]
    [InlineData("UPDATE t SET c = 1;", "UPDATE")]
    [InlineData("DELETE FROM t;", "DELETE")]
    public async Task DmlStatement_IsRejectedAndPointsAtDeployScripts(string sql, string expectedName)
    {
        var builder = BuilderFor(("Seed.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SqlSourceException.ImperativeStatement, ex.Code);
        Assert.Contains(expectedName, ex.Message);
        Assert.Contains("post-deploy", ex.Message);
    }

    /// <summary>
    /// A query declares nothing, so it is rejected too — but it writes no data, so it is not
    /// sent to the deploy-script remedy, which would be advising the author to keep a
    /// statement that does nothing either way.
    /// </summary>
    [Theory]
    [InlineData("SELECT 1;")]
    [InlineData("SELECT * FROM t;")]
    public async Task Query_IsRejectedWithoutTheSeedDataRemedy(string sql)
    {
        var builder = BuilderFor(("A.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SqlSourceException.ImperativeStatement, ex.Code);
        Assert.Contains("SELECT", ex.Message);
        Assert.DoesNotContain("seed", ex.Message);
    }

    /// <summary>
    /// The whole point of the new diagnostic is that it points at the offending statement, so
    /// the IDE can put a squiggle on it. The old SQ0001 carried no position whatsoever.
    /// </summary>
    [Fact]
    public async Task Diagnostic_IsAnchoredToTheStatement()
    {
        var builder = BuilderFor(("A.sql", """
CREATE TABLE t (id integer PRIMARY KEY);

ALTER TABLE t ADD COLUMN c integer;
"""));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal(3, ex.Line);
        Assert.Equal(1, ex.Column);
    }

    /// <summary>
    /// One imperative statement must not stop the rest of the file being reported: a build
    /// surfaces every problem at once rather than one per rebuild (issue #61).
    /// </summary>
    [Fact]
    public async Task MultipleImperativeStatements_AreAllReported()
    {
        var builder = BuilderFor(
            ("A.sql", "ALTER TABLE t ADD COLUMN c integer;"),
            ("B.sql", "DROP TABLE u;"));

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        var errors = ex.InnerExceptions.Cast<SqlSourceException>().ToList();

        Assert.Equal(2, errors.Count);
        Assert.All(errors, e => Assert.Equal(SqlSourceException.ImperativeStatement, e.Code));
        Assert.Contains(errors, e => e.SourceFile == "A.sql");
        Assert.Contains(errors, e => e.SourceFile == "B.sql");
    }

    /// <summary>
    /// An imperative statement is an error, but the CREATEs around it still model — so the
    /// build reports the ALTER alongside any other problem instead of the ALTER masking them.
    /// </summary>
    [Fact]
    public async Task ImperativeStatement_DoesNotMaskOtherErrors()
    {
        var builder = BuilderFor(
            ("A.sql", "DROP TABLE t;"),
            ("Book.sql", "CREATE TABLE book (id integer PRIMARY KEY, author_id integer REFERENCES author (id));"));

        var ex = await Assert.ThrowsAsync<AggregateException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        var errors = ex.InnerExceptions.Cast<SqlSourceException>().ToList();

        Assert.Contains(errors, e => e.Code == SqlSourceException.ImperativeStatement && e.SourceFile == "A.sql");
        Assert.Contains(errors, e => e.Code == SqlSourceException.UnresolvedReference && e.SourceFile == "Book.sql");
    }

    /// <summary>
    /// Declarative source is unaffected — this is a rejection of imperative statements, not a
    /// new restriction on what a project may declare.
    /// </summary>
    [Fact]
    public async Task DeclarativeSource_StillBuilds()
    {
        var builder = BuilderFor(("A.sql", """
CREATE SCHEMA app;

CREATE TABLE app.t (id integer PRIMARY KEY, name varchar(50) NOT NULL);

CREATE INDEX ix_t_name ON app.t (name);
"""));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Model.Elements);
        Assert.DoesNotContain(result.Warnings, w => w.Code == SqlSourceException.ImperativeStatement);
    }
}

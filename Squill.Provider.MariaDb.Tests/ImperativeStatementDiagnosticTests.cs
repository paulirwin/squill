using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Squill is declarative: source files state the desired end-state as CREATE, and the diff
/// engine generates the ALTER/DROP. An authored ALTER, DROP or DML statement in a compiled
/// source file is therefore a user error, and gets its own SQ0006 diagnostic saying so
/// (issue #125).
///
/// <para>
/// Before this, MariaDB reported an authored ALTER/DROP as an SQ1002 <em>warning</em> — the
/// wrong signal twice over: it said "not modeled by Squill", implying a gap in Squill rather
/// than a mistake in the source, and being a warning it let the build succeed while silently
/// ignoring the statement. DML was worse still: it never reached the mapper at all and
/// vanished with no diagnostic whatsoever.
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

        return new ParserWorkspaceModelBuilder(
            workspace, new AntlrMariaDbParser(), new MariaDb12DatabaseSchemaProvider());
    }

    [Theory]
    [InlineData("ALTER TABLE t ADD COLUMN c int;", "ALTER TABLE")]
    [InlineData("ALTER TABLE t DROP COLUMN c;", "ALTER TABLE")]
    [InlineData("DROP TABLE t;", "DROP TABLE")]
    [InlineData("DROP INDEX ix ON t;", "DROP INDEX")]
    [InlineData("TRUNCATE TABLE t;", "TRUNCATE")]
    public async Task SchemaChangingStatement_IsRejectedWithSq0006(string sql, string expectedName)
    {
        var builder = BuilderFor(("A.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SqlSourceException.ImperativeStatement, ex.Code);
        Assert.Equal("A.sql", ex.SourceFile);

        Assert.Contains(expectedName, ex.Message);
        Assert.Contains("declarative", ex.Message);
        Assert.Contains("CREATE", ex.Message);

        // The old wording blamed Squill for a gap; the new wording blames the statement.
        Assert.DoesNotContain("not modeled by Squill", ex.Message);
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
    /// A query declares nothing and writes nothing, so neither of the other remedies fits:
    /// "express this as CREATE" would imply it was trying to state an end-state, and "move it
    /// to a deploy script" would be advising the author to keep a statement that does nothing
    /// either way. It gets its own wording telling them to remove it.
    /// </summary>
    [Theory]
    [InlineData("SELECT 1;")]
    [InlineData("SELECT * FROM t;")]
    public async Task Query_IsRejectedWithItsOwnRemedy(string sql)
    {
        var builder = BuilderFor(("A.sql", sql));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal(SqlSourceException.ImperativeStatement, ex.Code);
        Assert.Contains("Remove it", ex.Message);

        // Neither of the other two remedies, both of which would be wrong here.
        Assert.DoesNotContain("seed", ex.Message);
        Assert.DoesNotContain("end-state", ex.Message);
    }

    /// <summary>
    /// It must be an error, not the SQ1002 warning it used to be: a warning let the build
    /// succeed while the statement was silently discarded.
    /// </summary>
    [Fact]
    public async Task ImperativeStatement_IsNoLongerAWarning()
    {
        var builder = BuilderFor(("A.sql", "ALTER TABLE t ADD COLUMN c int;"));

        await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Diagnostic_IsAnchoredToTheStatement()
    {
        var builder = BuilderFor(("A.sql", """
CREATE TABLE t (id int NOT NULL PRIMARY KEY);

ALTER TABLE t ADD COLUMN c int;
"""));

        var ex = await Assert.ThrowsAsync<SqlSourceException>(
            () => builder.ExtractModelAsync(TestContext.Current.CancellationToken));

        Assert.Equal(3, ex.Line);
        Assert.Equal(1, ex.Column);
    }

    [Fact]
    public async Task MultipleImperativeStatements_AreAllReported()
    {
        var builder = BuilderFor(
            ("A.sql", "ALTER TABLE t ADD COLUMN c int;"),
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
    /// Declarative source is unaffected — this is a rejection of imperative statements, not a
    /// new restriction on what a project may declare.
    /// </summary>
    [Fact]
    public async Task DeclarativeSource_StillBuilds()
    {
        var builder = BuilderFor(("A.sql", """
CREATE TABLE t (id int NOT NULL PRIMARY KEY, name varchar(50) NOT NULL);

CREATE INDEX ix_t_name ON t (name);
"""));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Model.Elements);
    }
}

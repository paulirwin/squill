using Squill.MariaDbParser.Syntax;

namespace Squill.MariaDbParser.Tests;

/// <summary>
/// The parser side of issue #125: an imperative statement maps to an
/// <see cref="ImperativeStatement"/> marker rather than the <see cref="UnmodeledStatement"/>
/// it used to, so the model builder rejects it with SQ0006 instead of warning SQ1002.
/// </summary>
public class ImperativeStatementTests
{
    private static ImperativeStatement ParseOne(string sql)
    {
        var root = new AntlrMariaDbParser().Parse(sql);

        return Assert.IsType<ImperativeStatement>(Assert.Single(root.Statements));
    }

    [Theory]
    [InlineData("ALTER TABLE t ADD COLUMN c int;", "ALTER TABLE")]
    [InlineData("DROP TABLE t;", "DROP TABLE")]
    [InlineData("DROP INDEX ix ON t;", "DROP INDEX")]
    [InlineData("TRUNCATE TABLE t;", "TRUNCATE")]
    public void SchemaChangingStatement_MapsToMarkerNamingTheStatement(string sql, string expectedName)
    {
        var statement = ParseOne(sql);

        Assert.Equal(expectedName, statement.Name);
        Assert.False(statement.IsDml);
    }

    /// <summary>
    /// DML never reached the mapper before — the parser only yielded DDL contexts — so an
    /// authored INSERT vanished silently. It is now carried through to be rejected.
    /// </summary>
    [Theory]
    [InlineData("INSERT INTO t (c) VALUES (1);", "INSERT")]
    [InlineData("UPDATE t SET c = 1;", "UPDATE")]
    [InlineData("DELETE FROM t;", "DELETE")]
    public void DmlStatement_MapsToMarkerFlaggedAsDml(string sql, string expectedName)
    {
        var statement = ParseOne(sql);

        Assert.Equal(expectedName, statement.Name);
        Assert.True(statement.IsDml);
    }

    /// <summary>
    /// A query declares nothing, so it has no business in a schema file and is rejected like
    /// the rest — but it writes no data, so it is not flagged as such and does not get the
    /// "move this into a post-deploy script" remedy, which would be advising the author to
    /// keep a statement that does nothing either way.
    /// </summary>
    [Theory]
    [InlineData("SELECT 1;")]
    [InlineData("SELECT * FROM t;")]
    public void Query_IsRejectedButNotAsData(string sql)
    {
        var statement = ParseOne(sql);

        Assert.Equal("SELECT", statement.Name);
        Assert.False(statement.IsDml);
    }

    [Fact]
    public void Marker_CarriesTheStatementPosition()
    {
        var root = new AntlrMariaDbParser().Parse("""
CREATE TABLE t (id int NOT NULL PRIMARY KEY);

DROP TABLE u;
""");

        var imperative = Assert.IsType<ImperativeStatement>(root.Statements[1]);

        Assert.Equal(3, imperative.Line);
        Assert.Equal(1, imperative.Column);
    }

    [Fact]
    public void DeclarativeStatements_AreUnaffected()
    {
        var root = new AntlrMariaDbParser().Parse("""
CREATE TABLE t (id int NOT NULL PRIMARY KEY);
ALTER TABLE t ADD COLUMN c int;
CREATE INDEX ix ON t (id);
""");

        Assert.Collection(root.Statements,
            s => Assert.IsType<CreateTableStatement>(s),
            s => Assert.IsType<ImperativeStatement>(s),
            s => Assert.IsType<CreateIndexStatement>(s));
    }
}

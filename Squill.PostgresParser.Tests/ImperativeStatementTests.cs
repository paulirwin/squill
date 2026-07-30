using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// The parser side of issue #125: an imperative statement maps to an
/// <see cref="ImperativeStatement"/> marker carrying its name and position, so the model
/// builder can reject it with a source-anchored SQ0006 error.
///
/// <para>
/// Previously nothing mapped these, so <c>VisitRoot</c> threw
/// "Expected VisitStmt to return a Statement" — aborting the whole file and losing the
/// position along with it.
/// </para>
/// </summary>
public class ImperativeStatementTests
{
    private static ImperativeStatement ParseOne(string sql)
    {
        var root = new AntlrPostgresParser().Parse(sql);

        return Assert.IsType<ImperativeStatement>(Assert.Single(root.Statements));
    }

    [Theory]
    [InlineData("ALTER TABLE t ADD COLUMN c integer;", "ALTER TABLE")]
    [InlineData("ALTER INDEX ix RENAME TO ix2;", "ALTER INDEX")]
    [InlineData("ALTER SCHEMA s RENAME TO s2;", "ALTER SCHEMA")]
    [InlineData("DROP TABLE t;", "DROP TABLE")]
    [InlineData("DROP INDEX ix;", "DROP INDEX")]
    [InlineData("DROP VIEW v;", "DROP VIEW")]
    [InlineData("TRUNCATE TABLE t;", "TRUNCATE")]
    public void SchemaChangingStatement_MapsToMarkerNamingTheStatement(string sql, string expectedName)
    {
        var statement = ParseOne(sql);

        Assert.Equal(expectedName, statement.Name);
        Assert.Equal(ImperativeKind.SchemaChange, statement.Kind);
    }

    [Theory]
    [InlineData("INSERT INTO t (c) VALUES (1);", "INSERT")]
    [InlineData("UPDATE t SET c = 1;", "UPDATE")]
    [InlineData("DELETE FROM t;", "DELETE")]
    public void DmlStatement_MapsToMarkerFlaggedAsDml(string sql, string expectedName)
    {
        var statement = ParseOne(sql);

        Assert.Equal(expectedName, statement.Name);
        Assert.Equal(ImperativeKind.DataChange, statement.Kind);
    }

    /// <summary>
    /// A query declares nothing, so it has no business in a schema file and is rejected like
    /// the rest — but it writes no data, so it is not flagged as such and does not get the
    /// "move this into a post-deploy script" remedy, which would be advising the author to
    /// keep a statement that does nothing either way.
    /// </summary>
    [Theory]
    [InlineData("SELECT 1;", "SELECT")]
    [InlineData("SELECT * FROM t;", "SELECT")]
    [InlineData("VALUES (1), (2);", "VALUES")]
    public void Query_IsRejectedButNotAsData(string sql, string expectedName)
    {
        var statement = ParseOne(sql);

        Assert.Equal(expectedName, statement.Name);
        Assert.Equal(ImperativeKind.Query, statement.Kind);
    }

    /// <summary>
    /// A CTE takes the kind of the statement it feeds, not of the leading WITH. Postgres lets
    /// a CTE write — both in the WITH clause (<c>WITH d AS (DELETE … RETURNING *)</c>) and in
    /// the statement it feeds — so classifying on the WITH alone sent a data-modifying CTE to
    /// the "express this as CREATE" remedy, which is the wrong fix for it.
    /// </summary>
    [Theory]
    [InlineData("WITH x AS (SELECT 1) INSERT INTO t (c) SELECT * FROM x;")]
    [InlineData("WITH x AS (SELECT 1) UPDATE t SET c = 1;")]
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) INSERT INTO u SELECT * FROM d;")]
    public void DataModifyingCte_IsClassifiedAsDataChange(string sql)
    {
        var statement = ParseOne(sql);

        Assert.Equal(ImperativeKind.DataChange, statement.Kind);
    }

    [Fact]
    public void ReadOnlyCte_IsClassifiedAsAQuery()
    {
        var statement = ParseOne("WITH x AS (SELECT 1) SELECT * FROM x;");

        Assert.Equal(ImperativeKind.Query, statement.Kind);
    }

    /// <summary>
    /// The position is the whole reason this is a marker rather than a throw from the visitor.
    /// </summary>
    [Fact]
    public void Marker_CarriesTheStatementPosition()
    {
        var root = new AntlrPostgresParser().Parse("""
CREATE TABLE t (id integer PRIMARY KEY);

DROP TABLE u;
""");

        var imperative = Assert.IsType<ImperativeStatement>(root.Statements[1]);

        Assert.Equal(3, imperative.Line);
        Assert.Equal(1, imperative.Column);
    }

    /// <summary>
    /// The marker must not swallow the declarative statements around it — those still map to
    /// their own syntax nodes, so a file with one bad statement still reports the rest.
    /// </summary>
    [Fact]
    public void DeclarativeStatements_AreUnaffected()
    {
        var root = new AntlrPostgresParser().Parse("""
CREATE TABLE t (id integer PRIMARY KEY);
ALTER TABLE t ADD COLUMN c integer;
CREATE INDEX ix ON t (id);
""");

        Assert.Collection(root.Statements,
            s => Assert.IsType<CreateTableStatement>(s),
            s => Assert.IsType<ImperativeStatement>(s),
            s => Assert.IsType<CreateIndexStatement>(s));
    }
}

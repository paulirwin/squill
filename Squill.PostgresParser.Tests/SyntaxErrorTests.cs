using Squill.PostgresParser;
using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

public class SyntaxErrorTests
{
    [Fact]
    public void Parse_InvalidSqlOnFirstLine_ThrowsWithPosition()
    {
        var parser = new AntlrPostgresParser();

        var ex = Assert.Throws<PostgresParseException>(() => parser.Parse("this is not valid sql;"));

        Assert.Equal(1, ex.Line);
        Assert.NotNull(ex.Column);
        Assert.True(ex.Column >= 1, "Column should be 1-based.");
    }

    [Fact]
    public void Parse_InvalidStatementAfterValidOne_ReportsLineOfTheError()
    {
        const string sql = """
CREATE TABLE foo
(
    id integer PRIMARY KEY
);
CREATE bogus;
""";
        var parser = new AntlrPostgresParser();

        var ex = Assert.Throws<PostgresParseException>(() => parser.Parse(sql));

        Assert.Equal(5, ex.Line);
        Assert.Contains("bogus", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ValidSql_SetsStatementPosition()
    {
        const string sql = """
CREATE TABLE foo
(
    id integer PRIMARY KEY
);
""";
        var parser = new AntlrPostgresParser();

        var root = parser.Parse(sql);

        var statement = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
        Assert.Equal(1, statement.Line);
        Assert.Equal(1, statement.Column);
    }

    [Fact]
    public void Parse_InlineForeignKey_SetsConstraintPosition()
    {
        const string sql = """
CREATE TABLE book
(
    id integer PRIMARY KEY,
    author_id integer REFERENCES author (id)
);
""";
        var parser = new AntlrPostgresParser();

        var root = parser.Parse(sql);

        var statement = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
        var column = statement.Elements.OfType<ColumnDefinition>().Single(c => c.Name.Name == "author_id");
        var fk = Assert.Single(column.Constraints.OfType<ForeignKeyColumnConstraint>());

        Assert.Equal(4, fk.Line);
        Assert.True(fk.Column >= 1);
    }

    [Fact]
    public void Parse_TableLevelForeignKey_SetsConstraintPosition()
    {
        const string sql = """
CREATE TABLE book
(
    id integer PRIMARY KEY,
    author_id integer,
    FOREIGN KEY (author_id) REFERENCES author (id)
);
""";
        var parser = new AntlrPostgresParser();

        var root = parser.Parse(sql);

        var statement = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
        var fk = Assert.Single(statement.Elements.OfType<ForeignKeyTableConstraint>());

        Assert.Equal(5, fk.Line);
        Assert.True(fk.Column >= 1);
    }
}

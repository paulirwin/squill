using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

public class CreateSchemaTests
{
    [Fact]
    public void CreateSchema_Simple()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE SCHEMA staging;";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateSchemaStatement>(Assert.Single(root.Statements));

        Assert.Equal("staging", stmt.Name.Name);
        Assert.False(stmt.IfNotExists);
    }

    [Fact]
    public void CreateSchema_IfNotExists()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE SCHEMA IF NOT EXISTS reporting;";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateSchemaStatement>(Assert.Single(root.Statements));

        Assert.Equal("reporting", stmt.Name.Name);
        Assert.True(stmt.IfNotExists);
    }

    [Fact]
    public void CreateSchema_QuotedName()
    {
        var parser = new AntlrPostgresParser();

        const string text = """CREATE SCHEMA "My Schema";""";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateSchemaStatement>(Assert.Single(root.Statements));

        Assert.Equal("My Schema", stmt.Name.Name);
    }
}

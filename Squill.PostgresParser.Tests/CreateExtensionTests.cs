using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

public class CreateExtensionTests
{
    [Fact]
    public void CreateExtension_Simple()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE EXTENSION vector;";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateExtensionStatement>(Assert.Single(root.Statements));

        Assert.Equal("vector", stmt.Name.Name);
        Assert.False(stmt.IfNotExists);
        Assert.Null(stmt.Schema);
        Assert.Null(stmt.Version);
    }

    [Fact]
    public void CreateExtension_IfNotExists()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE EXTENSION IF NOT EXISTS citext;";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateExtensionStatement>(Assert.Single(root.Statements));

        Assert.Equal("citext", stmt.Name.Name);
        Assert.True(stmt.IfNotExists);
    }

    [Fact]
    public void CreateExtension_QuotedName()
    {
        var parser = new AntlrPostgresParser();

        const string text = """CREATE EXTENSION "uuid-ossp";""";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateExtensionStatement>(Assert.Single(root.Statements));

        Assert.Equal("uuid-ossp", stmt.Name.Name);
    }

    [Fact]
    public void CreateExtension_WithSchemaAndVersion()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE EXTENSION citext WITH SCHEMA public VERSION '1.6';";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateExtensionStatement>(Assert.Single(root.Statements));

        Assert.Equal("citext", stmt.Name.Name);
        Assert.Equal("public", stmt.Schema?.Name);
        Assert.Equal("1.6", stmt.Version);
    }
}

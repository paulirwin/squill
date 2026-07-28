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

    /// <summary>
    /// CASCADE parses and is carried on the statement. It is not modeled: it is a one-shot
    /// instruction about how to *install* the extension's dependencies, not a property of the
    /// deployed object, so there is nothing in the catalog to compare it against (issue #143).
    /// </summary>
    [Fact]
    public void CreateExtension_Cascade()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE EXTENSION earthdistance CASCADE;";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateExtensionStatement>(Assert.Single(root.Statements));

        Assert.Equal("earthdistance", stmt.Name.Name);
        Assert.True(stmt.Cascade);
        Assert.Null(stmt.FromVersion);
    }

    [Fact]
    public void CreateExtension_CascadeWithSchemaAndVersion()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE EXTENSION earthdistance WITH SCHEMA public VERSION '1.1' CASCADE;";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateExtensionStatement>(Assert.Single(root.Statements));

        Assert.Equal("earthdistance", stmt.Name.Name);
        Assert.Equal("public", stmt.Schema?.Name);
        Assert.Equal("1.1", stmt.Version);
        Assert.True(stmt.Cascade);
    }

    /// <summary>
    /// FROM names the pre-9.1 "unpackaged" version an extension is being upgraded from. Like
    /// CASCADE it describes the installation, not the resulting object, so it parses and is
    /// carried but not modeled (issue #143).
    /// </summary>
    [Fact]
    public void CreateExtension_FromVersion()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE EXTENSION hstore FROM unpackaged;";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateExtensionStatement>(Assert.Single(root.Statements));

        Assert.Equal("hstore", stmt.Name.Name);
        Assert.Equal("unpackaged", stmt.FromVersion);
        Assert.False(stmt.Cascade);
    }

    [Fact]
    public void CreateExtension_FromVersionAsStringLiteral()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE EXTENSION hstore FROM 'unpackaged';";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateExtensionStatement>(Assert.Single(root.Statements));

        Assert.Equal("unpackaged", stmt.FromVersion);
    }
}

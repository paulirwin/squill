using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// Parser tests for <c>CREATE TYPE ... AS ENUM</c> and <c>CREATE DOMAIN</c> (issue #75).
/// </summary>
public class CreateTypeTests
{
    [Fact]
    public void CreateEnumType_Simple()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE TYPE mpaa_rating AS ENUM ('G', 'PG', 'PG-13', 'R', 'NC-17');";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateEnumTypeStatement>(Assert.Single(root.Statements));

        Assert.Equal("mpaa_rating", stmt.Name.ToString());
        Assert.Equal(["G", "PG", "PG-13", "R", "NC-17"], stmt.Labels);
    }

    [Fact]
    public void CreateEnumType_SchemaQualified()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE TYPE inventory.status AS ENUM ('active', 'retired');";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateEnumTypeStatement>(Assert.Single(root.Statements));

        Assert.Equal("inventory.status", stmt.Name.ToString());
        Assert.Equal(["active", "retired"], stmt.Labels);
    }

    [Fact]
    public void CreateEnumType_SingleLabel()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE TYPE singleton AS ENUM ('one');";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateEnumTypeStatement>(Assert.Single(root.Statements));

        Assert.Equal("singleton", stmt.Name.ToString());
        Assert.Equal(["one"], stmt.Labels);
    }

    [Fact]
    public void CreateDomain_WithNamedCheck()
    {
        var parser = new AntlrPostgresParser();

        const string text =
            "CREATE DOMAIN year AS integer CONSTRAINT year_check CHECK (VALUE >= 1901 AND VALUE <= 2155);";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateDomainStatement>(Assert.Single(root.Statements));

        Assert.Equal("year", stmt.Name.ToString());
        Assert.Equal("integer", stmt.DataType.TypeName);

        var constraint = Assert.Single(stmt.Constraints);
        var named = Assert.IsType<NamedColumnConstraint>(constraint);
        Assert.Equal("year_check", named.Name);
        Assert.IsType<CheckColumnConstraint>(named.Constraint);
    }

    [Fact]
    public void CreateDomain_WithUnnamedCheck()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE DOMAIN positive_int AS integer CHECK (VALUE > 0);";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateDomainStatement>(Assert.Single(root.Statements));

        Assert.Equal("positive_int", stmt.Name.ToString());
        Assert.Equal("integer", stmt.DataType.TypeName);

        var constraint = Assert.Single(stmt.Constraints);
        Assert.IsType<CheckColumnConstraint>(constraint);
    }

    [Fact]
    public void CreateDomain_WithNotNull()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE DOMAIN nonempty AS text NOT NULL;";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateDomainStatement>(Assert.Single(root.Statements));

        Assert.Equal("nonempty", stmt.Name.ToString());
        Assert.Equal("text", stmt.DataType.TypeName);

        var constraint = Assert.Single(stmt.Constraints);
        var nullable = Assert.IsType<NullableColumnConstraint>(constraint);
        Assert.False(nullable.Nullable);
    }

    [Fact]
    public void CreateDomain_NoConstraints()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE DOMAIN us_postal_code AS text;";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateDomainStatement>(Assert.Single(root.Statements));

        Assert.Equal("us_postal_code", stmt.Name.ToString());
        Assert.Equal("text", stmt.DataType.TypeName);
        Assert.Empty(stmt.Constraints);
    }
}

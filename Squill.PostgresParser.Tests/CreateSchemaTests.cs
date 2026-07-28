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

    /// <summary>
    /// <c>CREATE SCHEMA name AUTHORIZATION role</c> — the schema is named explicitly and the
    /// role owns it. The name is modeled as usual; the owning role is carried on the statement
    /// but not modeled, since Squill does not manage roles (issue #143).
    /// </summary>
    [Fact]
    public void CreateSchema_NameWithAuthorization()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE SCHEMA staging AUTHORIZATION joe;";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateSchemaStatement>(Assert.Single(root.Statements));

        Assert.Equal("staging", stmt.Name.Name);
        Assert.Equal("joe", stmt.Authorization);
    }

    /// <summary>
    /// The name-less form: <c>CREATE SCHEMA AUTHORIZATION joe</c> creates a schema *named after
    /// the role*, per the PostgreSQL docs. So the role doubles as the schema name here.
    /// </summary>
    [Fact]
    public void CreateSchema_AuthorizationOnly_TakesItsNameFromTheRole()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE SCHEMA AUTHORIZATION joe;";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateSchemaStatement>(Assert.Single(root.Statements));

        Assert.Equal("joe", stmt.Name.Name);
        Assert.Equal("joe", stmt.Authorization);
    }

    [Fact]
    public void CreateSchema_IfNotExistsWithAuthorization()
    {
        var parser = new AntlrPostgresParser();

        const string text = "CREATE SCHEMA IF NOT EXISTS staging AUTHORIZATION joe;";

        var root = parser.Parse(text);
        var stmt = Assert.IsType<CreateSchemaStatement>(Assert.Single(root.Statements));

        Assert.Equal("staging", stmt.Name.Name);
        Assert.Equal("joe", stmt.Authorization);
        Assert.True(stmt.IfNotExists);
    }

    /// <summary>
    /// CURRENT_USER and SESSION_USER are valid role specs. They resolve at execution time, so
    /// the resulting schema name is not knowable at build time — unlike a named role, which
    /// gives a deterministic name. Rejected rather than guessed.
    /// </summary>
    [Theory]
    [InlineData("CREATE SCHEMA AUTHORIZATION CURRENT_USER;")]
    [InlineData("CREATE SCHEMA AUTHORIZATION SESSION_USER;")]
    public void CreateSchema_AuthorizationOnly_NonConstantRole_IsRejected(string text)
    {
        var parser = new AntlrPostgresParser();

        var ex = Assert.ThrowsAny<Exception>(() => parser.Parse(text));

        Assert.Contains("AUTHORIZATION", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

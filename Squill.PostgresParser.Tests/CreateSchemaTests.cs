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
    /// CURRENT_USER and SESSION_USER are valid role specs. In the <em>name-less</em> form they
    /// resolve at execution time and the schema takes its name from the role, so the resulting
    /// schema name is not knowable at build time — unlike a named role, which gives a
    /// deterministic name. Rejected rather than guessed.
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

    /// <summary>
    /// With an explicit schema name, a non-constant role is accepted (issue #166): the schema's
    /// name is <c>staging</c> whoever deploys it, and only its <em>ownership</em> is resolved at
    /// deploy time — which is unmodeled for a named role too (SQ1002, #143). So a non-constant
    /// role costs nothing extra here. The token is carried verbatim so the warning can name what
    /// was dropped; it is not emitted in the generated DDL, which is a bare
    /// <c>CREATE SCHEMA IF NOT EXISTS</c> either way.
    /// </summary>
    [Theory]
    [InlineData("CREATE SCHEMA staging AUTHORIZATION CURRENT_USER;", "CURRENT_USER")]
    [InlineData("CREATE SCHEMA staging AUTHORIZATION SESSION_USER;", "SESSION_USER")]
    public void CreateSchema_NamedWithNonConstantRole_IsAccepted(string text, string expectedRole)
    {
        var parser = new AntlrPostgresParser();

        var root = parser.Parse(text);

        var stmt = Assert.IsType<CreateSchemaStatement>(Assert.Single(root.Statements));

        Assert.Equal("staging", stmt.Name.Name);
        Assert.Equal(expectedRole, stmt.Authorization);
    }

    /// <summary>
    /// The rejection of the name-less form must name the way out, since the named form is now
    /// available and is what a user in this position almost always wants.
    /// </summary>
    [Fact]
    public void CreateSchema_AuthorizationOnly_Rejection_SuggestsNamingTheSchema()
    {
        var parser = new AntlrPostgresParser();

        var ex = Assert.ThrowsAny<Exception>(
            () => parser.Parse("CREATE SCHEMA AUTHORIZATION CURRENT_USER;"));

        Assert.Contains("CREATE SCHEMA <name> AUTHORIZATION CURRENT_USER", ex.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// IF NOT EXISTS composes with a non-constant role, the same as it does with a named one.
    /// </summary>
    [Fact]
    public void CreateSchema_IfNotExistsNamedWithNonConstantRole_IsAccepted()
    {
        var parser = new AntlrPostgresParser();

        var root = parser.Parse(
            "CREATE SCHEMA IF NOT EXISTS staging AUTHORIZATION CURRENT_USER;");

        var stmt = Assert.IsType<CreateSchemaStatement>(Assert.Single(root.Statements));

        Assert.Equal("staging", stmt.Name.Name);
        Assert.Equal("CURRENT_USER", stmt.Authorization);
        Assert.True(stmt.IfNotExists);
    }

    /// <summary>
    /// A quoted role is a role <em>name</em>, not the keyword, even when it spells one: Postgres
    /// treats <c>"current_user"</c> as an ordinary identifier. It must round-trip as written.
    /// </summary>
    [Fact]
    public void CreateSchema_QuotedRoleSpellingAKeyword_IsARoleName()
    {
        var parser = new AntlrPostgresParser();

        var root = parser.Parse("CREATE SCHEMA staging AUTHORIZATION \"current_user\";");

        var stmt = Assert.IsType<CreateSchemaStatement>(Assert.Single(root.Statements));

        Assert.Equal("staging", stmt.Name.Name);
        Assert.Equal("\"current_user\"", stmt.Authorization);
    }
}

using Squill.PostgresParser.Syntax;
using Squill.TestFramework;

namespace Squill.PostgresParser.Tests;

public class CreateViewTests
{
    private static CreateViewStatement ParseOne(string text)
        => ParseAssertions.Single<CreateViewStatement>(new AntlrPostgresParser().Parse(text).Statements);

    [Fact]
    public void CreateView_PlainColumns()
    {
        var stmt = ParseOne("CREATE VIEW active_users AS SELECT id, name FROM users;");

        Assert.Equal("active_users", stmt.Name.Segments[^1].Name);
        Assert.False(stmt.OrReplace);
        Assert.Empty(stmt.ColumnNames);
        Assert.Equal(new[] { "id", "name" }, stmt.SelectColumns.Select(i => i.DerivedName));
    }

    [Fact]
    public void CreateView_OrReplace()
    {
        var stmt = ParseOne("CREATE OR REPLACE VIEW v AS SELECT id FROM users;");

        Assert.True(stmt.OrReplace);
    }

    [Fact]
    public void CreateView_SchemaQualifiedName()
    {
        var stmt = ParseOne("CREATE VIEW reporting.totals AS SELECT id FROM users;");

        Assert.Equal("totals", stmt.Name.Segments[^1].Name);
        Assert.Equal("reporting", stmt.Name.Segments[0].Name);
    }

    [Fact]
    public void CreateView_ExplicitColumnList()
    {
        var stmt = ParseOne("CREATE VIEW v (a, b) AS SELECT id, name FROM users;");

        Assert.Equal(new[] { "a", "b" }, stmt.ColumnNames.Select(i => i.Name));
    }

    [Fact]
    public void CreateView_AliasedColumns()
    {
        var stmt = ParseOne("CREATE VIEW v AS SELECT id AS the_id, name AS the_name FROM users;");

        Assert.Equal(new[] { "the_id", "the_name" }, stmt.SelectColumns.Select(i => i.DerivedName));
    }

    [Fact]
    public void CreateView_AliasedExpression_TakesItsAlias()
    {
        var stmt = ParseOne("CREATE VIEW v AS SELECT qty * 2 AS doubled FROM orders;");

        var column = Assert.Single(stmt.SelectColumns);
        Assert.Equal("doubled", column.DerivedName);
    }

    [Fact]
    public void CreateView_UnaliasedExpression_HasNoDerivableName()
    {
        // An expression without an alias gives Squill no name to model the column under; the
        // model builder turns this into a source-anchored build error.
        var stmt = ParseOne("CREATE VIEW v AS SELECT qty * 2 FROM orders;");

        var column = Assert.Single(stmt.SelectColumns);
        Assert.Null(column.DerivedName);
        Assert.False(column.IsWildcard);
    }

    [Fact]
    public void CreateView_Wildcard()
    {
        var stmt = ParseOne("CREATE VIEW v AS SELECT * FROM users;");

        var column = Assert.Single(stmt.SelectColumns);
        Assert.True(column.IsWildcard);
        Assert.Null(column.Qualifier);
    }

    [Fact]
    public void CreateView_QualifiedWildcard_CarriesItsQualifier()
    {
        var stmt = ParseOne("CREATE VIEW v AS SELECT u.* FROM users u;");

        var column = Assert.Single(stmt.SelectColumns);
        Assert.True(column.IsWildcard);
        Assert.Equal("u", column.Qualifier);
    }

    [Fact]
    public void CreateView_QualifiedColumn_TakesTheBareColumnName()
    {
        var stmt = ParseOne("CREATE VIEW v AS SELECT users.id, users.name FROM users;");

        Assert.Equal(new[] { "id", "name" }, stmt.SelectColumns.Select(i => i.DerivedName));
    }

    [Fact]
    public void CreateView_RecordsSourceTables()
    {
        var stmt = ParseOne("CREATE VIEW v AS SELECT id FROM users;");

        var table = Assert.Single(stmt.SourceTables);
        Assert.Equal("users", table.Segments[^1].Name);
    }

    [Fact]
    public void CreateView_BodyIsCapturedVerbatim()
    {
        var stmt = ParseOne("CREATE VIEW v AS SELECT id, name FROM users WHERE active;");

        Assert.Equal("SELECT id, name FROM users WHERE active", stmt.Body);
    }

    [Fact]
    public void CreateView_WhereClauseDoesNotAffectColumns()
    {
        var stmt = ParseOne("CREATE VIEW v AS SELECT id FROM users WHERE qty > 10;");

        var column = Assert.Single(stmt.SelectColumns);
        Assert.Equal("id", column.DerivedName);
    }

    [Fact]
    public void CreateView_RecordsSourcePosition()
    {
        var root = new AntlrPostgresParser().Parse("""
            CREATE TABLE users (id integer);

            CREATE VIEW v AS SELECT id FROM users;
            """);

        var stmt = Assert.IsType<CreateViewStatement>(root.Statements[^1]);

        Assert.Equal(3, stmt.Line);
        Assert.Equal(1, stmt.Column);
    }
}

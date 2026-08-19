using Squill.MariaDbParser;
using Squill.MariaDbParser.Syntax;
using Squill.TestFramework;

namespace Squill.MariaDbParser.Tests;

/// <summary>
/// Parser-level tests for CREATE VIEW (issue #42), asserting the syntax tree the mapper
/// produces. Model-level concerns (column derivation, SELECT * expansion) are covered in
/// Squill.Provider.MariaDb.Tests.
/// </summary>
public class CreateViewTests
{
    private static CreateViewStatement ParseOne(string text)
    {
        var root = new AntlrMariaDbParser().Parse(text);

        return Assert.IsType<CreateViewStatement>(Assert.Single(root.Statements));
    }

    [Fact]
    public void CreateView_PlainColumns()
    {
        var statement = ParseOne("CREATE VIEW active_users AS SELECT id, name FROM users;");

        Assert.Equal("active_users", statement.Name.Name);
        Assert.False(statement.OrReplace);
        Assert.Empty(statement.ColumnNames);
        Assert.Equal(new[] { "id", "name" }, statement.SelectColumns.Select(i => i.DerivedName));
    }

    [Fact]
    public void CreateView_OrReplace()
    {
        var statement = ParseOne("CREATE OR REPLACE VIEW v AS SELECT id FROM users;");

        Assert.True(statement.OrReplace);
    }

    [Fact]
    public void CreateView_ExplicitColumnList()
    {
        var statement = ParseOne("CREATE VIEW v (a, b) AS SELECT id, name FROM users;");

        Assert.Equal(new[] { "a", "b" }, statement.ColumnNames.Select(i => i.Name));
    }

    [Fact]
    public void CreateView_AliasedColumns()
    {
        var statement = ParseOne(
            "CREATE VIEW v AS SELECT id AS the_id, name AS the_name FROM users;");

        Assert.Equal(new[] { "the_id", "the_name" }, statement.SelectColumns.Select(i => i.DerivedName));
    }

    [Fact]
    public void CreateView_AliasedExpression_TakesItsAlias()
    {
        var statement = ParseOne("CREATE VIEW v AS SELECT qty * 2 AS doubled FROM orders;");

        var column = Assert.Single(statement.SelectColumns);
        Assert.Equal("doubled", column.DerivedName);
    }

    [Fact]
    public void CreateView_UnaliasedExpression_HasNoDerivableName()
    {
        var statement = ParseOne("CREATE VIEW v AS SELECT qty * 2 FROM orders;");

        var column = Assert.Single(statement.SelectColumns);
        Assert.Null(column.DerivedName);
        Assert.False(column.IsWildcard);
    }

    [Fact]
    public void CreateView_Wildcard()
    {
        var statement = ParseOne("CREATE VIEW v AS SELECT * FROM users;");

        var column = Assert.Single(statement.SelectColumns);
        Assert.True(column.IsWildcard);
        Assert.Null(column.Qualifier);
    }

    [Fact]
    public void CreateView_QualifiedWildcard_CarriesItsQualifier()
    {
        var statement = ParseOne("CREATE VIEW v AS SELECT users.* FROM users;");

        var column = Assert.Single(statement.SelectColumns);
        Assert.True(column.IsWildcard);
        Assert.Equal("users", column.Qualifier);
    }

    [Fact]
    public void CreateView_QualifiedColumn_TakesTheBareColumnName()
    {
        var statement = ParseOne("CREATE VIEW v AS SELECT users.id, users.name FROM users;");

        Assert.Equal(new[] { "id", "name" }, statement.SelectColumns.Select(i => i.DerivedName));
    }

    [Fact]
    public void CreateView_BacktickQuotedNames_AreUnquoted()
    {
        var statement = ParseOne("CREATE VIEW `my view` AS SELECT `id` FROM `users`;");

        Assert.Equal("my view", statement.Name.Name);

        var column = Assert.Single(statement.SelectColumns);
        Assert.Equal("id", column.DerivedName);
    }

    [Fact]
    public void CreateView_RecordsSourceTables()
    {
        var statement = ParseOne("CREATE VIEW v AS SELECT id FROM users;");

        var table = Assert.Single(statement.SourceTables);
        Assert.Equal("users", table.Name);
    }

    [Fact]
    public void CreateView_BodyIsCapturedVerbatim()
    {
        var statement = ParseOne("CREATE VIEW v AS SELECT id, name FROM users WHERE active = 1;");

        Assert.Equal("SELECT id, name FROM users WHERE active = 1", statement.Body);
    }

    [Fact]
    public void CreateView_WhereClauseDoesNotAffectColumns()
    {
        var statement = ParseOne("CREATE VIEW v AS SELECT id FROM users WHERE qty > 10;");

        var column = Assert.Single(statement.SelectColumns);
        Assert.Equal("id", column.DerivedName);
    }

    [Fact]
    public void CreateView_RecordsSourcePosition()
    {
        var root = new AntlrMariaDbParser().Parse("""
            CREATE TABLE users (id int);

            CREATE VIEW v AS SELECT id FROM users;
            """);

        var statement = Assert.IsType<CreateViewStatement>(root.Statements[^1]);

        Assert.Equal(3, statement.Line);
        Assert.Equal(1, statement.Column);
    }

    // Issue #208: ALGORITHM, DEFINER, SQL SECURITY and WITH CHECK OPTION were all parsed
    // and dropped. Measured against mariadb:latest and mysql:latest.

    [Fact]
    public void CreateView_NoOptions_AreNull()
    {
        var statement = ParseOne("CREATE VIEW v AS SELECT id FROM users;");

        Assert.Null(statement.CheckOption);
        Assert.Null(statement.SecurityType);
        Assert.Null(statement.Algorithm);
        Assert.Null(statement.Definer);
    }

    [Fact]
    public void CreateView_BareCheckOption_IsCascaded()
    {
        // Measured on both engines: a bare WITH CHECK OPTION is reported as CASCADED, the
        // same normalization PostgreSQL applies.
        var statement = ParseOne("CREATE VIEW v AS SELECT id FROM users WITH CHECK OPTION;");

        Assert.Equal("CASCADED", statement.CheckOption);
    }

    [Fact]
    public void CreateView_CascadedCheckOption()
    {
        var statement = ParseOne("CREATE VIEW v AS SELECT id FROM users WITH CASCADED CHECK OPTION;");

        Assert.Equal("CASCADED", statement.CheckOption);
    }

    [Fact]
    public void CreateView_LocalCheckOption()
    {
        var statement = ParseOne("CREATE VIEW v AS SELECT id FROM users WITH LOCAL CHECK OPTION;");

        Assert.Equal("LOCAL", statement.CheckOption);
    }

    [Fact]
    public void CreateView_SqlSecurityInvoker()
    {
        var statement = ParseOne("CREATE SQL SECURITY INVOKER VIEW v AS SELECT id FROM users;");

        Assert.Equal("INVOKER", statement.SecurityType);
    }

    [Fact]
    public void CreateView_SqlSecurityDefiner()
    {
        var statement = ParseOne("CREATE SQL SECURITY DEFINER VIEW v AS SELECT id FROM users;");

        Assert.Equal("DEFINER", statement.SecurityType);
    }

    [Theory]
    [InlineData("MERGE")]
    [InlineData("TEMPTABLE")]
    [InlineData("UNDEFINED")]
    public void CreateView_Algorithm(string algorithm)
    {
        var statement = ParseOne($"CREATE ALGORITHM={algorithm} VIEW v AS SELECT id FROM users;");

        Assert.Equal(algorithm, statement.Algorithm);
    }

    [Fact]
    public void CreateView_Algorithm_IsUpperCased()
    {
        var statement = ParseOne("CREATE ALGORITHM=merge VIEW v AS SELECT id FROM users;");

        Assert.Equal("MERGE", statement.Algorithm);
    }

    [Fact]
    public void CreateView_Definer_IsCaptured()
    {
        // Captured so the model builder can warn rather than let it vanish; ownership itself
        // is out of scope here (issue #221).
        var statement = ParseOne("CREATE DEFINER=`admin`@`localhost` VIEW v AS SELECT id FROM users;");

        Assert.NotNull(statement.Definer);
    }

    [Fact]
    public void CreateView_AllOptionsTogether()
    {
        var statement = ParseOne(
            "CREATE ALGORITHM=TEMPTABLE DEFINER=`admin`@`localhost` SQL SECURITY INVOKER "
            + "VIEW v AS SELECT id FROM users WITH LOCAL CHECK OPTION;");

        Assert.Equal("TEMPTABLE", statement.Algorithm);
        Assert.Equal("INVOKER", statement.SecurityType);
        Assert.Equal("LOCAL", statement.CheckOption);
        Assert.NotNull(statement.Definer);
    }
}

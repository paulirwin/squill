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

    // Issue #208: the clauses that decide how a view executes were parsed and dropped.
    // Measured on PostgreSQL 18: both spellings land in the same pg_class.reloptions entry,
    // so the parser normalizes them onto one facet rather than keeping two.

    [Fact]
    public void CreateView_NoCheckOption_IsNull()
    {
        var stmt = ParseOne("CREATE VIEW v AS SELECT id FROM users;");

        Assert.Null(stmt.CheckOption);
    }

    [Fact]
    public void CreateView_BareCheckOption_IsCascaded()
    {
        // Measured: PostgreSQL stores a bare WITH CHECK OPTION as check_option=cascaded, so
        // recording "CASCADED" here is what lets it match the extracted view.
        var stmt = ParseOne("CREATE VIEW v AS SELECT id FROM users WITH CHECK OPTION;");

        Assert.Equal("CASCADED", stmt.CheckOption);
    }

    [Fact]
    public void CreateView_CascadedCheckOption()
    {
        var stmt = ParseOne("CREATE VIEW v AS SELECT id FROM users WITH CASCADED CHECK OPTION;");

        Assert.Equal("CASCADED", stmt.CheckOption);
    }

    [Fact]
    public void CreateView_LocalCheckOption()
    {
        var stmt = ParseOne("CREATE VIEW v AS SELECT id FROM users WITH LOCAL CHECK OPTION;");

        Assert.Equal("LOCAL", stmt.CheckOption);
    }

    [Fact]
    public void CreateView_CheckOptionAsReloption_NormalizesToTheClauseForm()
    {
        // WITH (check_option='local') and WITH LOCAL CHECK OPTION are indistinguishable in
        // the catalog (measured), so they must parse to the same value.
        var stmt = ParseOne("CREATE VIEW v WITH (check_option='local') AS SELECT id FROM users;");

        Assert.Equal("LOCAL", stmt.CheckOption);
    }

    [Fact]
    public void CreateView_SecurityInvoker_True()
    {
        var stmt = ParseOne("CREATE VIEW v WITH (security_invoker=true) AS SELECT id FROM users;");

        Assert.True(stmt.SecurityInvoker);
    }

    [Fact]
    public void CreateView_SecurityInvoker_ExplicitFalseIsKept()
    {
        // Measured: PostgreSQL records security_invoker=false in reloptions rather than
        // dropping it, so an explicitly-written default is distinguishable from an absent
        // one and must not be folded into "unset".
        var stmt = ParseOne("CREATE VIEW v WITH (security_invoker=false) AS SELECT id FROM users;");

        Assert.False(stmt.SecurityInvoker);
    }

    [Fact]
    public void CreateView_SecurityInvoker_AbsentIsNull()
    {
        var stmt = ParseOne("CREATE VIEW v AS SELECT id FROM users;");

        Assert.Null(stmt.SecurityInvoker);
    }

    [Fact]
    public void CreateView_SecurityBarrier_True()
    {
        var stmt = ParseOne("CREATE VIEW v WITH (security_barrier=true) AS SELECT id FROM users;");

        Assert.True(stmt.SecurityBarrier);
    }

    [Fact]
    public void CreateView_BareReloptionMeansTrue()
    {
        // A boolean reloption written with no value is true, matching PostgreSQL.
        var stmt = ParseOne("CREATE VIEW v WITH (security_invoker) AS SELECT id FROM users;");

        Assert.True(stmt.SecurityInvoker);
    }

    [Fact]
    public void CreateView_SeveralReloptions()
    {
        var stmt = ParseOne(
            "CREATE VIEW v WITH (security_invoker=true, security_barrier=true) AS SELECT id FROM users;");

        Assert.True(stmt.SecurityInvoker);
        Assert.True(stmt.SecurityBarrier);
    }

    [Fact]
    public void CreateView_UnrecognizedReloption_IsCarriedForWarning()
    {
        var stmt = ParseOne("CREATE VIEW v WITH (autovacuum_enabled=true) AS SELECT id FROM users;");

        Assert.Contains("autovacuum_enabled", stmt.UnmodeledOptions);
    }
}

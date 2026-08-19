using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

public class CreateViewModelTests
{
    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            TestContext.Current.CancellationToken);

    private static async Task<Element> BuildViewAsync(string sql)
    {
        var model = await BuildModelAsync(sql);

        return Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlView);
    }

    private static IEnumerable<string?> ColumnNames(Element view)
        => view.GetRelationship(PostgresRelationshipNames.Columns)!
            .Entries.OfType<Element>().Select(i => i.Name);

    private const string Users =
        "CREATE TABLE users (id integer PRIMARY KEY, name varchar(50), active boolean);";

    [Fact]
    public async Task View_PlainColumns_AreModeledInOrder()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW active_users AS SELECT id, name FROM users;
            """);

        Assert.Equal("public.active_users", view.Name);
        Assert.Equal("public", PostgresModelFactory.GetSchema(view));
        Assert.Equal(new[] { "public.active_users.id", "public.active_users.name" }, ColumnNames(view));
    }

    [Fact]
    public async Task View_ExplicitColumnList_NamesTheColumns()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v (a, b) AS SELECT id, name FROM users;
            """);

        Assert.Equal(new[] { "public.v.a", "public.v.b" }, ColumnNames(view));
    }

    [Fact]
    public async Task View_AliasedColumns_TakeTheirAliases()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id AS the_id, name AS the_name FROM users;
            """);

        Assert.Equal(new[] { "public.v.the_id", "public.v.the_name" }, ColumnNames(view));
    }

    [Fact]
    public async Task View_AliasedExpression_IsModeled()
    {
        var view = await BuildViewAsync("""
            CREATE TABLE orders (id integer, qty integer);
            CREATE VIEW v AS SELECT id, qty * 2 AS doubled FROM orders;
            """);

        Assert.Equal(new[] { "public.v.id", "public.v.doubled" }, ColumnNames(view));
    }

    [Fact]
    public async Task View_Wildcard_ExpandsAgainstTheSourceTable()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT * FROM users;
            """);

        // The referenced table is declared in the project, so its columns are known.
        Assert.Equal(new[] { "public.v.id", "public.v.name", "public.v.active" }, ColumnNames(view));
    }

    [Fact]
    public async Task View_QualifiedWildcard_ExpandsAgainstTheSourceTable()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT users.* FROM users;
            """);

        Assert.Equal(new[] { "public.v.id", "public.v.name", "public.v.active" }, ColumnNames(view));
    }

    [Fact]
    public async Task View_DefinitionIsStoredVerbatim()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users WHERE active;
            """);

        Assert.Equal(
            "SELECT id FROM users WHERE active",
            view.GetProperty<string>(PostgresPropertyNames.Definition));
    }

    [Fact]
    public async Task View_SchemaQualified_CarriesItsSchema()
    {
        var view = await BuildViewAsync($"""
            CREATE SCHEMA reporting;
            {Users}
            CREATE VIEW reporting.totals AS SELECT id FROM users;
            """);

        Assert.Equal("reporting.totals", view.Name);
        Assert.Equal("reporting", PostgresModelFactory.GetSchema(view));
    }

    [Fact]
    public async Task View_InUndeclaredSchema_IsAnError()
    {
        var error = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync($"""
            {Users}
            CREATE VIEW reporting.totals AS SELECT id FROM users;
            """));

        Assert.Equal(SqlSourceException.UnresolvedReference, error.Code);
    }

    [Fact]
    public async Task View_OverAnUndeclaredTable_IsAnError()
    {
        var error = await Assert.ThrowsAsync<SqlSourceException>(() =>
            BuildModelAsync("CREATE VIEW v AS SELECT id FROM nonexistent;"));

        Assert.Equal(SqlSourceException.UnresolvedReference, error.Code);
        Assert.Contains("nonexistent", error.Message);
    }

    [Fact]
    public async Task View_UnaliasedExpression_IsReportedAsASourceError()
    {
        // An expression with no alias gives no name to model the column under. PostgreSQL
        // would invent one ("?column?"); Squill asks the author to be explicit instead.
        var error = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync("""
            CREATE TABLE orders (id integer, qty integer);
            CREATE VIEW v AS SELECT qty * 2 FROM orders;
            """));

        Assert.Contains("alias", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task View_WildcardOverAJoin_IsReportedAsASourceError()
    {
        // Which table an unqualified * expands over is ambiguous across a join, so this is
        // rejected rather than guessed at.
        var error = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync("""
            CREATE TABLE a (id integer);
            CREATE TABLE b (id integer);
            CREATE VIEW v AS SELECT * FROM a, b;
            """));

        Assert.Contains("*", error.Message);
    }

    [Fact]
    public async Task Views_AreOrderedAfterTables()
    {
        var model = await BuildModelAsync($"""
            CREATE VIEW v AS SELECT id FROM users;
            {Users}
            """);

        var viewIndex = model.Elements.ToList().FindIndex(i => i.Type == PostgresElementTypes.SqlView);
        var tableIndex = model.Elements.ToList().FindIndex(i => i.Type == PostgresElementTypes.SqlTable);

        Assert.True(tableIndex < viewIndex,
            "A view must be ordered after the tables it may reference.");
    }

    // Issue #208: the view clauses that decide how it executes. What is modeled here is
    // exactly what pg_class.reloptions reports back (measured on PostgreSQL 18), so a
    // declared view and its extracted counterpart hash-match.

    [Fact]
    public async Task View_NoOptions_RecordsNothing()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users;
            """);

        Assert.Null(view.GetProperty<string>(PostgresPropertyNames.CheckOption));
        Assert.Null(view.GetProperty<bool?>(PostgresPropertyNames.SecurityInvoker));
        Assert.Null(view.GetProperty<bool?>(PostgresPropertyNames.SecurityBarrier));
    }

    [Theory]
    [InlineData("WITH CHECK OPTION", "CASCADED")]
    [InlineData("WITH CASCADED CHECK OPTION", "CASCADED")]
    [InlineData("WITH LOCAL CHECK OPTION", "LOCAL")]
    public async Task View_CheckOptionClause_IsModeled(string clause, string expected)
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users WHERE active {clause};
            """);

        Assert.Equal(expected, view.GetProperty<string>(PostgresPropertyNames.CheckOption));
    }

    [Fact]
    public async Task View_CheckOptionReloption_ModelsTheSameFacet()
    {
        // Measured: WITH (check_option='local') and WITH LOCAL CHECK OPTION are the same
        // reloptions entry, so they must reach the same property or one of the two spellings
        // would re-diff against its own database.
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v WITH (check_option='local') AS SELECT id FROM users WHERE active;
            """);

        Assert.Equal("LOCAL", view.GetProperty<string>(PostgresPropertyNames.CheckOption));
    }

    [Fact]
    public async Task View_SecurityInvoker_IsModeled()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v WITH (security_invoker=true) AS SELECT id FROM users;
            """);

        Assert.True(view.GetProperty<bool?>(PostgresPropertyNames.SecurityInvoker));
    }

    [Fact]
    public async Task View_SecurityInvokerFalse_IsStillModeled()
    {
        // Measured: PostgreSQL records security_invoker=false in reloptions rather than
        // dropping it, so an explicitly written default is a different state from an absent
        // one and must not be folded away. This is the opposite of the MariaDB family.
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v WITH (security_invoker=false) AS SELECT id FROM users;
            """);

        Assert.False(view.GetProperty<bool?>(PostgresPropertyNames.SecurityInvoker));
    }

    [Fact]
    public async Task View_SecurityBarrier_IsModeled()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v WITH (security_barrier=true) AS SELECT id FROM users;
            """);

        Assert.True(view.GetProperty<bool?>(PostgresPropertyNames.SecurityBarrier));
    }

    [Fact]
    public async Task View_SecurityInvokerAbsentAndFalse_DifferInTheHash()
    {
        // The distinction the catalog draws has to survive into the model, or a view that
        // declares the default would compare equal to one that declares nothing and the
        // deploy would never correct it.
        var absent = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users;
            """);

        var explicitFalse = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v WITH (security_invoker=false) AS SELECT id FROM users;
            """);

        Assert.False(HashUtility.HashesEqual(absent.Hash, explicitFalse.Hash));
    }

    [Fact]
    public async Task View_CheckOption_ChangesTheHash()
    {
        var without = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users WHERE active;
            """);

        var with = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users WHERE active WITH CASCADED CHECK OPTION;
            """);

        Assert.False(HashUtility.HashesEqual(without.Hash, with.Hash));
    }
}

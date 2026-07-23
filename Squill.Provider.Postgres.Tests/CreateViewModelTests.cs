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
}

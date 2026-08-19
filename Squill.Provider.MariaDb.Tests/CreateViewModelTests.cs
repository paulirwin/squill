using Squill.Core;
using Squill.TestFramework;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

public class CreateViewModelTests
{
    private const string Users =
        "CREATE TABLE users (id int PRIMARY KEY, name varchar(50), active tinyint(1));";

    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser(), new MariaDb12DatabaseSchemaProvider()),
            TestContext.Current.CancellationToken);

    private static async Task<Element> BuildViewAsync(string sql)
    {
        var model = await BuildModelAsync(sql);

        return Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlView);
    }

    private static IEnumerable<string?> ColumnNames(Element view)
        => view.GetRelationship(MariaDbRelationshipNames.Columns)!
            .Entries.OfType<Element>().Select(i => i.Name);

    [Fact]
    public async Task View_PlainColumns_AreModeledInOrder()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW active_users AS SELECT id, name FROM users;
            """);

        // MariaDB objects are not schema-scoped within a database, so the name is bare.
        Assert.Equal("active_users", view.Name);
        Assert.Equal(new[] { "active_users.id", "active_users.name" }, ColumnNames(view));
    }

    [Fact]
    public async Task View_ExplicitColumnList_NamesTheColumns()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v (a, b) AS SELECT id, name FROM users;
            """);

        Assert.Equal(new[] { "v.a", "v.b" }, ColumnNames(view));
    }

    [Fact]
    public async Task View_AliasedColumns_TakeTheirAliases()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id AS the_id, name AS the_name FROM users;
            """);

        Assert.Equal(new[] { "v.the_id", "v.the_name" }, ColumnNames(view));
    }

    [Fact]
    public async Task View_AliasedExpression_IsModeled()
    {
        var view = await BuildViewAsync("""
            CREATE TABLE orders (id int, qty int);
            CREATE VIEW v AS SELECT id, qty * 2 AS doubled FROM orders;
            """);

        Assert.Equal(new[] { "v.id", "v.doubled" }, ColumnNames(view));
    }

    [Fact]
    public async Task View_Wildcard_ExpandsAgainstTheSourceTable()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT * FROM users;
            """);

        Assert.Equal(new[] { "v.id", "v.name", "v.active" }, ColumnNames(view));
    }

    [Fact]
    public async Task View_QualifiedWildcard_ExpandsAgainstTheSourceTable()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT users.* FROM users;
            """);

        Assert.Equal(new[] { "v.id", "v.name", "v.active" }, ColumnNames(view));
    }

    [Fact]
    public async Task View_DefinitionIsStoredVerbatim()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users WHERE active = 1;
            """);

        Assert.Equal(
            "SELECT id FROM users WHERE active = 1",
            view.GetProperty<string>(MariaDbPropertyNames.Definition));
    }

    [Fact]
    public async Task View_Definition_DoesNotParticipateInIdentity()
    {
        // The query is carried for scripting but must never affect the hash: both engines
        // rewrite it, so an extracted view carries no query at all.
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users;
            """);

        var extracted = MariaDbModelFactory.CreateView(
            SqlName.Object("v"), ["id"], definition: null);

        Assert.True(HashUtility.HashesEqual(view.Hash, extracted.Hash),
            "A view's definition must not take part in its identity.");
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
        var error = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync("""
            CREATE TABLE orders (id int, qty int);
            CREATE VIEW v AS SELECT qty * 2 FROM orders;
            """));

        Assert.Contains("alias", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task View_WildcardOverAJoin_IsReportedAsASourceError()
    {
        var error = await Assert.ThrowsAsync<SqlSourceException>(() => BuildModelAsync("""
            CREATE TABLE a (id int);
            CREATE TABLE b (id int);
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

        var elements = model.Elements.ToList();

        Assert.True(
            elements.FindIndex(i => i.Type == MariaDbElementTypes.SqlTable)
            < elements.FindIndex(i => i.Type == MariaDbElementTypes.SqlView),
            "A view must be ordered after the tables it may reference.");
    }

    // Issue #208: the view clauses that decide how it executes. Both sides of the round trip
    // have to agree, so what is modeled here is exactly what
    // information_schema.VIEWS reports back (measured on mariadb:latest and mysql:latest).

    private static async Task<(Element View, IReadOnlyList<SqlSourceDiagnostic> Warnings)>
        BuildViewWithWarningsAsync(string sql, MariaDbFamilyDatabaseSchemaProvider schemaProvider)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("View.sql", FileKind.Compile, sql));

        var builder = new ParserWorkspaceModelBuilder(
            workspace, new AntlrMariaDbParser(), schemaProvider);

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);
        var view = result.Model.Elements.Single(i => i.Type == MariaDbElementTypes.SqlView);

        return (view, result.Warnings);
    }

    [Fact]
    public async Task View_NoOptions_RecordsNothing()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users;
            """);

        Assert.Null(view.GetProperty<string>(MariaDbPropertyNames.CheckOption));
        Assert.Null(view.GetProperty<bool?>(MariaDbPropertyNames.IsSecurityInvoker));
        Assert.Null(view.GetProperty<string>(MariaDbPropertyNames.ViewAlgorithm));
    }

    [Theory]
    [InlineData("WITH CHECK OPTION", "CASCADED")]
    [InlineData("WITH CASCADED CHECK OPTION", "CASCADED")]
    [InlineData("WITH LOCAL CHECK OPTION", "LOCAL")]
    public async Task View_CheckOption_IsModeled(string clause, string expected)
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users WHERE active = 1 {clause};
            """);

        Assert.Equal(expected, view.GetProperty<string>(MariaDbPropertyNames.CheckOption));
    }

    [Fact]
    public async Task View_SqlSecurityInvoker_IsModeled()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE SQL SECURITY INVOKER VIEW v AS SELECT id FROM users;
            """);

        Assert.True(view.GetProperty<bool?>(MariaDbPropertyNames.IsSecurityInvoker));
    }

    [Fact]
    public async Task View_SqlSecurityDefiner_RecordsNothing()
    {
        // Measured: an explicitly written DEFINER is indistinguishable in the catalog from
        // declaring nothing, so recording it would make this view re-diff on every deploy.
        var view = await BuildViewAsync($"""
            {Users}
            CREATE SQL SECURITY DEFINER VIEW v AS SELECT id FROM users;
            """);

        Assert.Null(view.GetProperty<bool?>(MariaDbPropertyNames.IsSecurityInvoker));
    }

    [Fact]
    public async Task View_Algorithm_IsModeledOnMariaDb()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE ALGORITHM=TEMPTABLE VIEW v AS SELECT id FROM users;
            """);

        Assert.Equal("TEMPTABLE", view.GetProperty<string>(MariaDbPropertyNames.ViewAlgorithm));
    }

    [Fact]
    public async Task View_AlgorithmUndefined_RecordsNothing()
    {
        var view = await BuildViewAsync($"""
            {Users}
            CREATE ALGORITHM=UNDEFINED VIEW v AS SELECT id FROM users;
            """);

        Assert.Null(view.GetProperty<string>(MariaDbPropertyNames.ViewAlgorithm));
    }

    [Fact]
    public async Task View_Algorithm_IsNotModeledOnMySql_AndWarns()
    {
        // MySQL's information_schema.VIEWS has no ALGORITHM column (measured), so a modeled
        // algorithm could never be read back and would re-diff forever.
        var (view, warnings) = await BuildViewWithWarningsAsync($"""
            {Users}
            CREATE ALGORITHM=TEMPTABLE VIEW v AS SELECT id FROM users;
            """, new MySql9DatabaseSchemaProvider());

        Assert.Null(view.GetProperty<string>(MariaDbPropertyNames.ViewAlgorithm));

        var warning = Assert.Single(warnings, w => w.Message.Contains("ALGORITHM"));

        Assert.Equal(SqlSourceDiagnostic.UnmodeledConstruct, warning.Code);
    }

    [Fact]
    public async Task View_Definer_IsNotModeled_AndWarns()
    {
        // Ownership is the broader question issue #221 covers; what matters here is that it
        // does not vanish silently, which is what issue #208 reported.
        var (_, warnings) = await BuildViewWithWarningsAsync($"""
            {Users}
            CREATE DEFINER=`admin`@`localhost` VIEW v AS SELECT id FROM users;
            """, new MariaDb12DatabaseSchemaProvider());

        var warning = Assert.Single(warnings, w => w.Message.Contains("DEFINER"));

        Assert.Equal(SqlSourceDiagnostic.UnmodeledConstruct, warning.Code);
    }

    [Fact]
    public async Task View_CheckOption_ChangesTheHash()
    {
        // The whole point of modeling these: a view whose CHECK OPTION changed must be
        // detected as changed. It was previously invisible to the diff.
        var without = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users WHERE active = 1;
            """);

        var with = await BuildViewAsync($"""
            {Users}
            CREATE VIEW v AS SELECT id FROM users WHERE active = 1 WITH CASCADED CHECK OPTION;
            """);

        Assert.False(HashUtility.HashesEqual(without.Hash, with.Hash));
    }
}

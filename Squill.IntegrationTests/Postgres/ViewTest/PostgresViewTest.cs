using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.ViewTest;

public class PostgresViewTest : PostgresIntegrationTestBase
{
    // Full round trip for views (issue #42): parse SQL into a model, publish it into a
    // fresh database, re-extract, and assert the views survive and the model hashes match.
    //
    // This is the test that proves the design holds. A view's query cannot round-trip —
    // PostgreSQL rewrites it when it stores it — so a view is modeled by its name and
    // column list, and the query is carried for scripting only. Hashes matching here is
    // what shows those facets really do agree between the two builders, including a
    // SELECT * expanded from declared columns and an aliased expression.
    [Fact]
    public async Task ViewRoundTrip_ModelHashesMatchAfterPublish()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.ViewTest.Views.sql", FileKind.Compile));

        var model = (await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken)).Model;

        AssertViews(model);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            var comparison = SchemaCompare.Compare(provider, model, emptyModel);

            await testDb.PublishAsync(comparison, TestContext.Current.CancellationToken);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            AssertViews(publishedModel);

            Assert.True(
                HashUtility.HashesEqual(model.Hash, publishedModel.Hash),
                "Parsed and extracted model hashes do not match");

            // Redeploying the same source must be a no-op. This is the assertion that
            // would fail if a view's (rewritten) query took part in the comparison.
            var republish = SchemaCompare.Compare(provider, model, publishedModel);

            Assert.Empty(republish.Deltas);
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    // A view whose column list changed is dropped and recreated, and the redeployed
    // database must then match the new source. CREATE OR REPLACE VIEW cannot express this
    // change (PostgreSQL rejects a replace that renames or removes a column), which is why
    // the recreate is scripted as DROP + CREATE.
    [Fact]
    public async Task ChangedViewColumns_AreRecreatedOnPublish()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        const string table = "CREATE TABLE widget (id integer PRIMARY KEY, name text, qty integer);";

        var original = await BuildModelAsync($"{table} CREATE VIEW v AS SELECT id, name FROM widget;");
        var updated = await BuildModelAsync($"{table} CREATE VIEW v AS SELECT id, qty FROM widget;");

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, original, emptyModel),
                TestContext.Current.CancellationToken);

            var deployed = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, updated, deployed),
                TestContext.Current.CancellationToken);

            var republished = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            Assert.True(
                HashUtility.HashesEqual(updated.Hash, republished.Hash),
                "The redeployed model does not match the updated source");

            var view = Assert.Single(republished.Elements, i => i.Type == PostgresElementTypes.SqlView);

            Assert.Equal(new[] { "public.v.id", "public.v.qty" }, ColumnNames(view));
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    // A view in a non-public schema is created in it, alongside the tables it selects from.
    [Fact]
    public async Task ViewInNonPublicSchema_RoundTrips()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var model = await BuildModelAsync("""
            CREATE SCHEMA reporting;
            CREATE TABLE reporting.sale (id integer PRIMARY KEY, amount integer);
            CREATE VIEW reporting.sale_summary AS SELECT id, amount FROM reporting.sale;
            """);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, model, emptyModel),
                TestContext.Current.CancellationToken);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            var view = Assert.Single(publishedModel.Elements, i => i.Type == PostgresElementTypes.SqlView);

            Assert.Equal("reporting.sale_summary", view.Name);
            Assert.Equal("reporting", PostgresModelFactory.GetSchema(view));

            Assert.True(
                HashUtility.HashesEqual(model.Hash, publishedModel.Hash),
                "Parsed and extracted model hashes do not match");

            Assert.Empty(SchemaCompare.Compare(provider, model, publishedModel).Deltas);
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    // Issue #208. The property round-tripping is necessary but not sufficient: what the
    // clause is for is constraining writes through the view. This proves the deployed view
    // actually enforces it, which is what silently dropping the clause stopped happening.
    [Fact]
    public async Task DeployedCheckOption_RejectsANonConformingWrite()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var model = await BuildModelAsync(
            "CREATE TABLE widget (id integer PRIMARY KEY, active boolean NOT NULL);"
            + "CREATE VIEW active_widget AS SELECT id, active FROM widget WHERE active "
            + "WITH CASCADED CHECK OPTION;");

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, model, emptyModel),
                TestContext.Current.CancellationToken);

            await testDb.ConnectAsync(TestContext.Current.CancellationToken);

            // Conforming: the row satisfies the view's predicate.
            await testDb.RunScriptAsync(
                "INSERT INTO active_widget (id, active) VALUES (1, true);",
                cancellationToken: TestContext.Current.CancellationToken);

            // Non-conforming: the row would fall outside the view, so the server must refuse
            // it. Without the CHECK OPTION reaching the deployed view this insert succeeds.
            var rejected = await Assert.ThrowsAnyAsync<Exception>(() =>
                testDb.RunScriptAsync(
                    "INSERT INTO active_widget (id, active) VALUES (2, false);",
                    cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("check option", rejected.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task<Model> BuildModelAsync(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return (await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken)).Model;
    }

    private static IEnumerable<string?> ColumnNames(Element view)
        => view.GetRelationship(PostgresRelationshipNames.Columns)!
            .Entries.OfType<Element>().Select(i => i.Name);

    private static void AssertViews(Model model)
    {
        var views = model.Elements.Where(i => i.Type == PostgresElementTypes.SqlView).ToList();

        Assert.Equal(9, views.Count);

        var activeAuthor = Assert.Single(views, i => (string?)i.Name == "public.active_author");
        Assert.Equal(
            new[] { "public.active_author.author_id", "public.active_author.name" },
            ColumnNames(activeAuthor));

        // The explicit column list wins over the select list's own names.
        var authorLabel = Assert.Single(views, i => (string?)i.Name == "public.author_label");
        Assert.Equal(
            new[] { "public.author_label.id", "public.author_label.label" },
            ColumnNames(authorLabel));

        // The aliased expression is modeled under its alias.
        var bookStock = Assert.Single(views, i => (string?)i.Name == "public.book_stock");
        Assert.Equal(
            new[]
            {
                "public.book_stock.book_id",
                "public.book_stock.title",
                "public.book_stock.double_copies",
            },
            ColumnNames(bookStock));

        // SELECT * expanded to the table's columns, in declaration order.
        var allBooks = Assert.Single(views, i => (string?)i.Name == "public.all_books");
        Assert.Equal(
            new[]
            {
                "public.all_books.book_id",
                "public.all_books.author_id",
                "public.all_books.title",
                "public.all_books.copies",
            },
            ColumnNames(allBooks));

        // Issue #208: the execution and security clauses. These are asserted on both the
        // parsed and the extracted model (AssertViews runs against each), so a mismatch
        // between the two sides fails here rather than silently re-diffing on every deploy.
        var checkedView = Assert.Single(views, i => (string?)i.Name == "public.active_author_checked");
        Assert.Equal("CASCADED", checkedView.GetProperty<string>(PostgresPropertyNames.CheckOption));

        var localView = Assert.Single(views, i => (string?)i.Name == "public.active_author_local");
        Assert.Equal("LOCAL", localView.GetProperty<string>(PostgresPropertyNames.CheckOption));

        var invoker = Assert.Single(views, i => (string?)i.Name == "public.author_invoker");
        Assert.True(invoker.GetProperty<bool?>(PostgresPropertyNames.SecurityInvoker));

        // The explicitly written default, which PostgreSQL records rather than dropping.
        var invokerFalse = Assert.Single(views, i => (string?)i.Name == "public.author_invoker_false");
        Assert.False(invokerFalse.GetProperty<bool?>(PostgresPropertyNames.SecurityInvoker));

        var barrier = Assert.Single(views, i => (string?)i.Name == "public.author_barrier");
        Assert.True(barrier.GetProperty<bool?>(PostgresPropertyNames.SecurityBarrier));

        // A view that declares none records none, which is what keeps it hash-matching its
        // extracted counterpart.
        Assert.Null(activeAuthor.GetProperty<string>(PostgresPropertyNames.CheckOption));
        Assert.Null(activeAuthor.GetProperty<bool?>(PostgresPropertyNames.SecurityInvoker));
    }
}

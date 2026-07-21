using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.ProcedureTest;

public class PostgresProcedureTest : PostgresIntegrationTestBase
{
    // Full round trip for stored procedures (issue #41): parse SQL into a model, publish
    // it into a fresh database, re-extract, and assert the procedures survive and the
    // model hashes match. This is what proves the parsed and extracted representations
    // agree — in particular that the verbatim body and the normalized argument signature
    // (varchar(100) becomes `character varying`) are recorded identically on both sides.
    [Fact]
    public async Task ProcedureRoundTrip_ModelHashesMatchAfterPublish()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.ProcedureTest.Procedures.sql", FileKind.Compile));

        var model = (await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken)).Model;

        AssertProcedures(model);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            var comparison = SchemaCompare.Compare(provider, model, emptyModel);

            await testDb.PublishAsync(comparison, TestContext.Current.CancellationToken);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            AssertProcedures(publishedModel);

            Assert.True(
                HashUtility.HashesEqual(model.Hash, publishedModel.Hash),
                "Parsed and extracted model hashes do not match");

            // Redeploying the same source must be a no-op: if any facet of a procedure did
            // not round-trip, the comparison would produce a spurious delta here.
            var republish = SchemaCompare.Compare(provider, model, publishedModel);

            Assert.Empty(republish.Deltas);
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    // A procedure whose body changed is redefined in place with CREATE OR REPLACE, and the
    // redeployed database must then match the new source.
    [Fact]
    public async Task ChangedProcedureBody_IsReplacedOnPublish()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var original = await BuildModelAsync(
            "CREATE PROCEDURE bump() LANGUAGE sql AS $$ SELECT 1 $$;");
        var updated = await BuildModelAsync(
            "CREATE PROCEDURE bump() LANGUAGE sql AS $$ SELECT 2 $$;");

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

            var procedure = Assert.Single(
                republished.Elements, i => i.Type == PostgresElementTypes.SqlProcedure);

            Assert.Contains("SELECT 2", procedure.GetProperty<string>(PostgresPropertyNames.Body));
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

    private static void AssertProcedures(Model model)
    {
        var procedures = model.Elements
            .Where(i => i.Type == PostgresElementTypes.SqlProcedure)
            .ToList();

        Assert.Equal(3, procedures.Count);

        // The varchar(100) parameter is recorded as plain `character varying`: PostgreSQL
        // discards a routine parameter's type modifier, so both builders must too.
        var addWidget = Assert.Single(
            procedures, i => (string?)i.Name == "public.add_widget(integer,character varying)");

        Assert.Equal("plpgsql", addWidget.GetProperty<string>(PostgresPropertyNames.Language));
        Assert.Equal(
            "IN widget_id integer, IN widget_name character varying",
            addWidget.GetProperty<string>(PostgresPropertyNames.Arguments));
        Assert.Contains(
            "INSERT INTO widgets (id, name) VALUES (widget_id, widget_name);",
            addWidget.GetProperty<string>(PostgresPropertyNames.Body));
        Assert.Null(addWidget.GetProperty<bool?>(PostgresPropertyNames.IsSecurityDefiner));

        // The overload is a separate object, identified by its differing signature.
        var addWidgetOverload = Assert.Single(
            procedures, i => (string?)i.Name == "public.add_widget(text)");

        Assert.Equal(true, addWidgetOverload.GetProperty<bool?>(PostgresPropertyNames.IsSecurityDefiner));

        var clearWidgets = Assert.Single(
            procedures, i => (string?)i.Name == "public.clear_widgets()");

        Assert.Equal("sql", clearWidgets.GetProperty<string>(PostgresPropertyNames.Language));
        Assert.Empty(clearWidgets.GetRequiredProperty<string>(PostgresPropertyNames.ArgumentTypes));
    }
}

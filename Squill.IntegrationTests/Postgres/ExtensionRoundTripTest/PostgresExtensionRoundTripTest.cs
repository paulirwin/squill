using Squill.Core;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.ExtensionRoundTripTest;

public class PostgresExtensionRoundTripTest : PostgresIntegrationTestBase
{
    // Full round trip for a Postgres extension (issue #9): build a model from SQL
    // (containing CREATE EXTENSION plus a table) against a temporary database,
    // publish it to a fresh target database, re-extract the target's model, and
    // assert the model hashes match. This exercises both extension publish scripting
    // and extension extraction from a real Postgres database. citext is a contrib
    // extension bundled in the stock postgres image, so no custom image is needed.
    [Fact]
    public async Task ExtensionRoundTrip_ModelHashesMatchAfterPublish()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var modelBuilder = new TemporaryDatabaseModelBuilder(provider);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.ExtensionRoundTripTest.WithExtension.sql", FileKind.Compile));

        var model = await modelBuilder.BuildModelAsync(workspace, TestContext.Current.CancellationToken);

        var extension = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlExtension);
        Assert.Equal("citext", extension.Name);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            // A fresh database only has the built-in plpgsql extension, which the
            // builder skips, so the target starts with no extension elements.
            Assert.DoesNotContain(emptyModel.Elements, i => i.Type == PostgresElementTypes.SqlExtension);

            var comparison = SchemaCompare.Compare(provider, model, emptyModel);

            await testDb.PublishAsync(comparison, TestContext.Current.CancellationToken);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            var publishedExtension = Assert.Single(
                publishedModel.Elements, i => i.Type == PostgresElementTypes.SqlExtension);
            Assert.Equal("citext", publishedExtension.Name);

            Assert.True(HashUtility.HashesEqual(model.Hash, publishedModel.Hash), "Model hashes do not match");
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }
}

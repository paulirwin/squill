using Squill.Core;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.MultiColumnTest;

public class PostgresMultiColumnIndexTest : PostgresIntegrationTestBase
{
    // Full round trip for a two-column index: build -> publish -> extract, asserting
    // the model hashes match and the index column order is preserved.
    [Fact]
    public async Task MultiColumnIndexRoundTrip_ModelHashesMatchAfterPublish()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.MultiColumnTest.CompositeKeyAndIndex.sql", FileKind.Compile));

        var model = await new TemporaryDatabaseModelBuilder(provider)
            .BuildModelAsync(workspace, TestContext.Current.CancellationToken);

        var index = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlIndex);
        var columnSpecs = index.GetRelationship(PostgresRelationshipNames.ColumnSpecifications)!;
        Assert.Equal(2, columnSpecs.Entries.Count);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            var comparison = SchemaCompare.Compare(provider, model, emptyModel);

            await testDb.PublishAsync(comparison, TestContext.Current.CancellationToken);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            Assert.True(HashUtility.HashesEqual(model.Hash, publishedModel.Hash), "Model hashes do not match");
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }
}

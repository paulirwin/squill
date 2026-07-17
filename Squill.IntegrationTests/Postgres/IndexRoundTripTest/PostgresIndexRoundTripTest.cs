using Squill.Core;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.IndexRoundTripTest;

public class PostgresIndexRoundTripTest : PostgresIntegrationTestBase
{
    // Full round trip: build a model from SQL (containing a table + index) against
    // a temporary database, publish it to a fresh target database, re-extract the
    // target's model, and assert the model hashes match. This exercises both index
    // publish scripting and index extraction from a real Postgres database.
    [Fact]
    public async Task IndexRoundTrip_ModelHashesMatchAfterPublish()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var modelBuilder = new TemporaryDatabaseModelBuilder(provider);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.IndexRoundTripTest.TableWithIndex.sql", FileKind.Compile));

        var model = await modelBuilder.BuildModelAsync(workspace, TestContext.Current.CancellationToken);

        var indexes = model.Elements.Where(i => i.Type == PostgresElementTypes.SqlIndex).ToList();
        Assert.Single(indexes);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            var comparison = SchemaCompare.Compare(provider, model, emptyModel);

            await testDb.PublishAsync(comparison, TestContext.Current.CancellationToken);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            var publishedIndexes = publishedModel.Elements
                .Where(i => i.Type == PostgresElementTypes.SqlIndex)
                .ToList();
            Assert.Single(publishedIndexes);

            Assert.True(HashUtility.HashesEqual(model.Hash, publishedModel.Hash), "Model hashes do not match");
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    // Round trip for a unique index that also specifies a method, descending order,
    // and nulls-last ordering, to verify those attributes survive publish + extract.
    [Fact]
    public async Task UniqueIndexRoundTrip_ModelHashesMatchAfterPublish()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var modelBuilder = new TemporaryDatabaseModelBuilder(provider);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.IndexRoundTripTest.TableWithUniqueIndex.sql", FileKind.Compile));

        var model = await modelBuilder.BuildModelAsync(workspace, TestContext.Current.CancellationToken);

        var index = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlIndex);
        Assert.Equal(true, index.GetProperty<bool?>(PostgresPropertyNames.IsUnique));
        Assert.Equal("btree", index.GetProperty<string>(PostgresPropertyNames.IndexMethod));

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

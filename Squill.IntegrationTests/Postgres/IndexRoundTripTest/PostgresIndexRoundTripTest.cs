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

    // Round trip for a partial (filtered) index (issue #8): the WHERE predicate must
    // survive publish + extraction. Both sides run through the temporary-database
    // builder, so the predicate is Postgres-canonicalized identically and the model
    // hashes match. Also asserts the extracted model actually carries a filter
    // predicate, so a silently-dropped WHERE clause would fail the test.
    [Fact]
    public async Task PartialIndexRoundTrip_ModelHashesMatchAfterPublish()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var modelBuilder = new TemporaryDatabaseModelBuilder(provider);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.IndexRoundTripTest.TableWithPartialIndex.sql", FileKind.Compile));

        var model = await modelBuilder.BuildModelAsync(workspace, TestContext.Current.CancellationToken);

        var index = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlIndex);
        var filterPredicate = index.GetProperty<string>(PostgresPropertyNames.FilterPredicate);
        Assert.False(string.IsNullOrEmpty(filterPredicate), "Partial index is missing its filter predicate");

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            var comparison = SchemaCompare.Compare(provider, model, emptyModel);

            await testDb.PublishAsync(comparison, TestContext.Current.CancellationToken);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            var publishedIndex = Assert.Single(
                publishedModel.Elements, i => i.Type == PostgresElementTypes.SqlIndex);
            Assert.False(
                string.IsNullOrEmpty(publishedIndex.GetProperty<string>(PostgresPropertyNames.FilterPredicate)),
                "Published partial index lost its filter predicate");

            Assert.True(HashUtility.HashesEqual(model.Hash, publishedModel.Hash), "Model hashes do not match");
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }
}

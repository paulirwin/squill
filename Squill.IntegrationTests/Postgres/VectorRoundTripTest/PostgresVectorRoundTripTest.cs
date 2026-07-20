using Squill.Core;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.VectorRoundTripTest;

// Full round trip for the pgvector vector type and an HNSW index (issue #10): build a
// model from SQL (CREATE EXTENSION vector, a table with a vector(3) column, and an HNSW
// index with an operator class and storage parameters) against a temporary database,
// publish it to a fresh target database, re-extract the target's model, and assert the
// model hashes match. It then exercises the published schema functionally — inserting
// vectors and running a cosine-distance nearest-neighbour query — to prove the emitted
// DDL is valid, executable Postgres and that vector search actually works.
//
// The stock postgres image does not ship pgvector, so this test uses the pgvector image.
public class PostgresVectorRoundTripTest : PostgresIntegrationTestBase
{
    // pgvector's official image; the pgNN tag pins the Postgres major version.
    protected override string DockerImageName => "pgvector/pgvector:pg17";

    [Fact]
    public async Task VectorRoundTrip_ModelHashesMatchAndSearchWorks()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var modelBuilder = new TemporaryDatabaseModelBuilder(provider);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.VectorRoundTripTest.WithVector.sql", FileKind.Compile));

        var model = await modelBuilder.BuildModelAsync(workspace, TestContext.Current.CancellationToken);

        // Sanity-check the built model: the extension, the vector column with its
        // dimension, and the HNSW index with operator class + storage parameters.
        var extension = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlExtension);
        Assert.Equal("vector", extension.Name);

        var index = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlIndex);
        Assert.Equal("hnsw", index.GetProperty<string>(PostgresPropertyNames.IndexMethod));
        Assert.Equal("m=16, ef_construction=64", index.GetProperty<string>(PostgresPropertyNames.StorageParameters));

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            var comparison = SchemaCompare.Compare(provider, model, emptyModel);

            await testDb.PublishAsync(comparison, TestContext.Current.CancellationToken);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            // The published database, re-extracted, must hash-match the source model —
            // proving the vector type and HNSW index round-trip exactly.
            Assert.True(HashUtility.HashesEqual(model.Hash, publishedModel.Hash), "Model hashes do not match");

            await AssertVectorSearchWorksAsync(testDb, TestContext.Current.CancellationToken);
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    // Inserts a few vectors and runs a cosine-distance nearest-neighbour query. This
    // confirms the published schema is functional end-to-end: the vector type accepts
    // data and the HNSW / vector_cosine_ops index supports a <=> similarity search.
    private static async Task AssertVectorSearchWorksAsync(IDatabase database, CancellationToken cancellationToken)
    {
        await database.ConnectAsync(cancellationToken);

        await database.RunScriptAsync(
            """
            INSERT INTO items (id, embedding) VALUES
                (1, '[1, 0, 0]'),
                (2, '[0, 1, 0]'),
                (3, '[0.9, 0.1, 0]');
            """,
            cancellationToken: cancellationToken);

        // The <=> operator is cosine distance (from vector_cosine_ops). Querying for the
        // vector nearest to [1, 0, 0] must return id 1 (identical direction) first.
        const string query =
            "SELECT id FROM items ORDER BY embedding <=> '[1, 0, 0]' LIMIT 1;";

        await using var reader = await database.RunScriptReaderAsync(query, cancellationToken: cancellationToken);

        Assert.True(await reader.ReadAsync(cancellationToken), "Vector search returned no rows");
        Assert.Equal(1, reader.GetInt32(0));
    }
}

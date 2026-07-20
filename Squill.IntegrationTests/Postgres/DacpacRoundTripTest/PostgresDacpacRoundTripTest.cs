using Squill.Core;
using Squill.Dacpac;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.DacpacRoundTripTest;

public class PostgresDacpacRoundTripTest : PostgresIntegrationTestBase
{
    // Full DACPAC round trip against real Postgres (issue #18): build a model from a
    // multi-object schema (tables, identity + PK, FK, index) extracted from a real
    // database, serialize it to a DACPAC, deserialize it back, and assert the
    // deserialized model's whole-model hash matches the original. This proves the
    // DACPAC format faithfully preserves a real, provider-produced model — the core
    // goal of the issue — not just a hand-built one.
    [Fact]
    public async Task DacpacRoundTrip_RealModel_HashMatches()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var modelBuilder = new TemporaryDatabaseModelBuilder(provider);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.DacpacRoundTripTest.Schema.sql", FileKind.Compile));

        var model = await modelBuilder.BuildModelAsync(workspace, TestContext.Current.CancellationToken);

        // Sanity-check we built a non-trivial model, so a regression that silently
        // produced an empty model wouldn't pass this test vacuously.
        Assert.Contains(model.Elements, e => e.Type == PostgresElementTypes.SqlTable);
        Assert.Contains(model.Elements, e => e.Type == PostgresElementTypes.SqlIndex);
        Assert.Contains(model.Elements, e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint);

        var originalHash = model.Hash;

        var metadata = new ModelMetadata { ProviderName = "Postgresql" };

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, model, stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var (deserializedMetadata, deserializedModel) =
            await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

        Assert.Equal("Postgresql", deserializedMetadata.ProviderName);
        Assert.True(
            HashUtility.HashesEqual(originalHash, deserializedModel.Hash),
            "Deserialized model hash must match the original model hash.");
    }
}

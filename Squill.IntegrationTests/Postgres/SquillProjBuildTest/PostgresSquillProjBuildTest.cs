using Squill.Core;
using Squill.Dacpac;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.SquillProjBuildTest;

public class PostgresSquillProjBuildTest : PostgresIntegrationTestBase
{
    // Full round trip through the MSBuild/SDK build path (issue #20): write the
    // declarative schema to .sql files on disk, build a real .dacpac file exactly the
    // way BuildDacpacTask does (DacpacBuilder.BuildToFileAsync over on-disk source
    // files), deserialize that .dacpac back to a model, publish it to a fresh real
    // Postgres database, re-extract the database's model, and assert the hashes match.
    //
    // This proves the DACPAC the .squillproj build produces is not just internally
    // consistent but is a faithful, deployable artifact against real Postgres — the
    // core promise of "build a .squillproj, get a deployable dacpac".
    [Fact]
    public async Task SquillProjBuild_ProducesDeployableDacpac_ThatRoundTripsAgainstPostgres()
    {
        var ct = TestContext.Current.CancellationToken;

        var tempDir = Directory.CreateTempSubdirectory("squill-squillproj-integration");
        try
        {
            // Arrange: lay the schema down as on-disk .sql source files, as a .squillproj
            // would contain them.
            var schema = await new EmbeddedResourceFile(
                    "Squill.IntegrationTests.Postgres.SquillProjBuildTest.Schema.sql", FileKind.Compile)
                .ReadAllTextAsync(ct);

            var sqlPath = Path.Combine(tempDir.FullName, "Schema.sql");
            await File.WriteAllTextAsync(sqlPath, schema, ct);

            // Act: build a real .dacpac file via the same code path the MSBuild task uses.
            var dacpacPath = Path.Combine(tempDir.FullName, "bin", "Debug", "TestDb.dacpac");
            var workspace = DacpacBuilder.CreateWorkspace([sqlPath]);
            var metadata = new ModelMetadata { ProviderName = "Postgresql", Name = "TestDb" };
            await DacpacBuilder.BuildToFileAsync(workspace, metadata, dacpacPath, ct);

            Assert.True(File.Exists(dacpacPath), "The build should have produced a .dacpac file.");

            // Deserialize the built DACPAC back into a model — this is what a deploy would do.
            Model dacpacModel;
            await using (var stream = File.OpenRead(dacpacPath))
            {
                (_, dacpacModel) = await DacpacSerializer.Deserialize(stream, ct);
            }

            // Sanity-check the model has the expected shape so a vacuously-empty model
            // can't pass the round trip.
            Assert.Contains(dacpacModel.Elements, e => e.Type == PostgresElementTypes.SqlTable);
            Assert.Contains(dacpacModel.Elements, e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint);
            Assert.Contains(dacpacModel.Elements, e => e.Type == PostgresElementTypes.SqlIndex);

            // Assert: publish the DACPAC's model to a real, empty Postgres database and
            // confirm the re-extracted model hash-matches the DACPAC's model.
            IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
            var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}", ct);
            var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

            try
            {
                var emptyModel = await dbModelBuilder.ExtractModelAsync(ct);
                var comparison = SchemaCompare.Compare(provider, dacpacModel, emptyModel);

                await testDb.PublishAsync(comparison, ct);

                var publishedModel = await dbModelBuilder.ExtractModelAsync(ct);

                // Compare the multiset of top-level element hashes rather than the
                // whole-model hash. The whole-model hash is order-sensitive (a Merkle
                // root over Elements in list order), and the parser emits elements in
                // source-statement order while the database builder emits them in its
                // own extraction order — so the two agree on content but not ordering.
                // Matching element-hash multisets proves every element round-tripped
                // faithfully, which is what "the DACPAC is deployable" requires.
                var dacpacElementHashes = ElementHashMultiset(dacpacModel);
                var publishedElementHashes = ElementHashMultiset(publishedModel);

                Assert.Equal(dacpacElementHashes, publishedElementHashes);
            }
            finally
            {
                await testDb.DropAsync(ct);
            }
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }

    // The sorted multiset of each top-level element's hash, as hex strings so the
    // collection compares by value. Order-independent by construction.
    private static List<string> ElementHashMultiset(Model model)
        => model.Elements
            .Select(e => Convert.ToHexString(e.Hash))
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToList();
}

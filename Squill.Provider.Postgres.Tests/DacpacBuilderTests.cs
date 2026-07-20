using Squill.Core;
using Squill.Dacpac;

namespace Squill.Provider.Postgres.Tests;

public class DacpacBuilderTests
{
    private const string SampleSchema = """
CREATE TABLE Foo
(
    id integer PRIMARY KEY,
    name varchar(100) NOT NULL
);
""";

    [Fact]
    public async Task BuildModelAsync_ParsesCompileFilesIntoModel()
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Foo.sql", FileKind.Compile, SampleSchema));

        var model = await DacpacBuilder.BuildModelAsync(workspace, TestContext.Current.CancellationToken);

        Assert.Contains(model.Elements, e => e.Type == PostgresElementTypes.SqlTable);
    }

    [Fact]
    public async Task BuildAsync_ProducesDacpacThatRoundTripsToSameModel()
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Foo.sql", FileKind.Compile, SampleSchema));

        var expectedModel = await DacpacBuilder.BuildModelAsync(workspace, TestContext.Current.CancellationToken);
        var expectedHash = expectedModel.Hash;

        var metadata = new ModelMetadata { ProviderName = "Postgresql" };

        await using var stream = new MemoryStream();
        await DacpacBuilder.BuildAsync(workspace, metadata, stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var (deserializedMetadata, deserializedModel) =
            await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

        Assert.Equal("Postgresql", deserializedMetadata.ProviderName);
        Assert.True(
            HashUtility.HashesEqual(expectedHash, deserializedModel.Hash),
            "DACPAC built by DacpacBuilder must round-trip to the same model hash.");
    }

    [Fact]
    public async Task BuildToFileAsync_WritesDacpacFileFromSourcePathsOnDisk()
    {
        var tempDir = Directory.CreateTempSubdirectory("squill-dacpacbuilder-test");
        try
        {
            var sqlPath = Path.Combine(tempDir.FullName, "Foo.sql");
            await File.WriteAllTextAsync(sqlPath, SampleSchema, TestContext.Current.CancellationToken);

            var workspace = DacpacBuilder.CreateWorkspace([sqlPath]);
            var metadata = new ModelMetadata { ProviderName = "Postgresql" };

            var outputPath = Path.Combine(tempDir.FullName, "bin", "Sample.dacpac");
            await DacpacBuilder.BuildToFileAsync(
                workspace, metadata, outputPath, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(outputPath), "DACPAC file should be written to the output path.");

            await using var stream = File.OpenRead(outputPath);
            var (_, model) = await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);
            Assert.Contains(model.Elements, e => e.Type == PostgresElementTypes.SqlTable);
        }
        finally
        {
            tempDir.Delete(recursive: true);
        }
    }
}

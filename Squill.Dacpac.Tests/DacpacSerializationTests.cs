using System.IO.Compression;
using Squill.Core;

namespace Squill.Dacpac.Tests;

public class DacpacSerializationTests
{
    [Fact]
    public async Task DacpacSerializer_Serialize_Postgres()
    {
        // Arrange
        var metadata = new ModelMetadata { ProviderName = "Postgresql" };
        var model = new Model(); // TODO.JB
        await using var stream = new MemoryStream();

        // Act
        await DacpacSerializer.Serialize(metadata, model, stream, CancellationToken.None);

        // Assert
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Equal(4, zip.Entries.Count);

        var originEntry = Assert.Single(zip.Entries.Where(e => e.Name == "Origin.xml"));
        var contentTypesEntry = Assert.Single(zip.Entries.Where(e => e.Name == "[Content Types].xml"));
        var dacMetadataEntry = Assert.Single(zip.Entries.Where(e => e.Name == "DacMetadata.xml"));
        var modelEntry = Assert.Single(zip.Entries.Where(e => e.Name == "model.xml"));


    }
}

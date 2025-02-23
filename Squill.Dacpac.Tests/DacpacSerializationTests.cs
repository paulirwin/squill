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
        var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Equal(4, zip.Entries.Count);
    }
}

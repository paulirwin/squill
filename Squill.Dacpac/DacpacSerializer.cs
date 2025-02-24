using System.IO.Compression;
using Squill.Core;

namespace Squill.Dacpac;

public static class DacpacSerializer
{
    public static Task Serialize(ModelMetadata metadata, Model model, Stream stream, CancellationToken cancellationToken = default)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        var originEntry = zip.CreateEntry("Origin.xml");
        var contentTypesEntry = zip.CreateEntry("[Content Types].xml");
        var dacMetadataEntry = zip.CreateEntry("DacMetadata.xml");
        var modelEntry = zip.CreateEntry("model.xml");

        return Task.CompletedTask;
    }

    public static Task<(ModelMetadata, Model)> Deserialize(Stream stream, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

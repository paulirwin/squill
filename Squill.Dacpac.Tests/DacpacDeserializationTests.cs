using System.IO.Compression;
using System.Xml.Linq;
using Squill.Core;

namespace Squill.Dacpac.Tests;

public class DacpacDeserializationTests
{
    [Fact]
    public async Task DacpacSerializer_Deserialize_RestoresMetadata()
    {
        var metadata = new ModelMetadata
        {
            ProviderName = "Postgresql",
            Name = "MyApp",
            Version = "2.3.4.5",
            Description = "A test application",
        };

        var model = new Model();
        model.Elements.Add(new Element("SqlTable") { Name = "public.Widgets" });

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, model, stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var (result, _) = await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

        Assert.Equal("Postgresql", result.ProviderName);
        Assert.Equal("MyApp", result.Name);
        Assert.Equal("2.3.4.5", result.Version);
        Assert.Equal("A test application", result.Description);
    }

    [Fact]
    public async Task DacpacSerializer_Deserialize_DetectsCorruptModel()
    {
        var metadata = new ModelMetadata { ProviderName = "Postgresql" };
        var model = new Model();
        model.Elements.Add(new Element("SqlTable") { Name = "public.Widgets" });

        await using var original = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, model, original, TestContext.Current.CancellationToken);

        // Rebuild the archive with a tampered model.xml but the original Origin.xml
        // (and thus the original checksum), so deserialization must reject it.
        var tampered = TamperModelPart(original.ToArray());

        await using var tamperedStream = new MemoryStream(tampered);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DacpacSerializer.Deserialize(tamperedStream, TestContext.Current.CancellationToken));
    }

    private static byte[] TamperModelPart(byte[] dacpac)
    {
        using var source = new MemoryStream(dacpac);
        using var sourceZip = new ZipArchive(source, ZipArchiveMode.Read);

        using var output = new MemoryStream();
        using (var outZip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in sourceZip.Entries)
            {
                var newEntry = outZip.CreateEntry(entry.FullName);
                using var reader = entry.Open();
                using var writer = newEntry.Open();

                if (entry.Name == "model.xml")
                {
                    // Add an extra element so the bytes (and checksum) differ.
                    var doc = XDocument.Load(reader);
                    var modelNode = doc.Root!.Element(doc.Root.Name.Namespace + "Model")!;
                    modelNode.Add(new XElement(
                        doc.Root.Name.Namespace + "Element",
                        new XAttribute("Type", "SqlTable"),
                        new XAttribute("Name", "public.Injected")));
                    doc.Save(writer);
                }
                else
                {
                    reader.CopyTo(writer);
                }
            }
        }

        return output.ToArray();
    }
}

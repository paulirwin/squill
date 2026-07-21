using System.IO.Compression;
using Squill.Core;

namespace Squill.Dacpac.Tests;

/// <summary>
/// Verifies the DACPAC records the target engine major version (issue #39) SSDT-style: as the
/// <c>DspName</c> attribute on the <c>model.xml</c> <c>DataSchemaModel</c> root, and that it
/// round-trips back onto the deserialized metadata.
/// </summary>
public class TargetVersionSerializationTests
{
    private static Model SampleModel()
    {
        var model = new Model();
        model.Elements.Add(new Element("SqlTable") { Name = "public.Widgets" });
        return model;
    }

    [Fact]
    public async Task Serialize_WritesSchemaProviderTypeNameAsDspName()
    {
        var metadata = new ModelMetadata { ProviderName = "Postgresql", TargetMajorVersion = 16 };

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, SampleModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var modelEntry = Assert.Single(zip.Entries, e => e.Name == "model.xml");

        await using var modelStream = modelEntry.Open();
        var doc = System.Xml.Linq.XDocument.Load(modelStream);
        var dspName = (string?)doc.Root!.Attribute("DspName");

        // The DspName is the fully-qualified name of the real schema-provider type.
        Assert.Equal(
            "Squill.Provider.Postgres.Postgresql16DatabaseSchemaProvider", dspName);
    }

    [Fact]
    public async Task Serialize_WithoutTargetVersion_WritesNoDspName()
    {
        var metadata = new ModelMetadata { ProviderName = "Postgresql" };

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, SampleModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var modelEntry = Assert.Single(zip.Entries, e => e.Name == "model.xml");

        await using var modelStream = modelEntry.Open();
        var doc = System.Xml.Linq.XDocument.Load(modelStream);

        Assert.Null(doc.Root!.Attribute("DspName"));
    }

    [Fact]
    public async Task Serialize_WithUnsupportedVersion_Throws()
    {
        var metadata = new ModelMetadata { ProviderName = "Postgresql", TargetMajorVersion = 999 };

        await using var stream = new MemoryStream();

        await Assert.ThrowsAsync<UnsupportedTargetVersionException>(() =>
            DacpacSerializer.Serialize(metadata, SampleModel(), stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RoundTrip_WithTargetVersion_RestoresMajorVersion()
    {
        var metadata = new ModelMetadata { ProviderName = "MariaDb", TargetMajorVersion = 11 };

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, SampleModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var (result, _) = await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

        Assert.Equal(11, result.TargetMajorVersion);
    }

    [Fact]
    public async Task RoundTrip_WithoutTargetVersion_LeavesMajorVersionNull()
    {
        var metadata = new ModelMetadata { ProviderName = "Postgresql" };

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, SampleModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var (result, _) = await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

        Assert.Null(result.TargetMajorVersion);
    }

    [Fact]
    public async Task RoundTrip_TargetVersion_DoesNotBreakModelChecksum()
    {
        // The DspName rides on model.xml, which is checksummed in Origin.xml. Deserialize
        // verifies that checksum, so a clean round-trip proves the stamp is included in the
        // hashed bytes rather than bolted on afterward.
        var metadata = new ModelMetadata { ProviderName = "Postgresql", TargetMajorVersion = 16 };
        var model = SampleModel();
        var originalHash = model.Hash;

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, model, stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var (_, deserialized) = await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

        Assert.True(HashUtility.HashesEqual(originalHash, deserialized.Hash));
    }
}

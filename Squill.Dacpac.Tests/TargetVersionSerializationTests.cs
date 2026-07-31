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
    // Spelled out rather than referenced from DacpacConstants (which is internal): this is
    // package wire format, so the test should fail if the attribute is ever renamed, not follow
    // the rename silently.
    private const string TargetVersionAttribute = "SquillTargetVersion";

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

    /// <summary>
    /// The minor must survive the package, since deploy-time enforcement is the reason for
    /// recording a target at all and a floor of 8.4 cannot be enforced from a stamp saying 8
    /// (issue #189).
    /// </summary>
    [Fact]
    public async Task RoundTrip_WithMinorVersion_RestoresBothComponents()
    {
        var metadata = new ModelMetadata
        {
            ProviderName = "MySql",
            TargetVersion = new TargetVersion(8, 4),
        };

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, SampleModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var (result, _) = await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

        Assert.Equal(new TargetVersion(8, 4), result.TargetVersion);
        Assert.Equal(8, result.TargetMajorVersion);
    }

    [Fact]
    public async Task RoundTrip_WithBareMajor_RestoresAFloorOfPointZero()
    {
        var metadata = new ModelMetadata
        {
            ProviderName = "MySql",
            TargetVersion = new TargetVersion(8, 0),
        };

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, SampleModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var (result, _) = await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

        Assert.Equal(new TargetVersion(8, 0), result.TargetVersion);
    }

    /// <summary>
    /// A zero minor writes no attribute at all, so a bare-major package keeps exactly the
    /// attribute set SSDT would produce. The minor attribute is Squill's own addition, and it
    /// should only appear when it is actually carrying information.
    /// </summary>
    [Fact]
    public async Task Serialize_WithBareMajor_WritesNoTargetVersionAttribute()
    {
        var metadata = new ModelMetadata
        {
            ProviderName = "MySql",
            TargetVersion = new TargetVersion(8, 0),
        };

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, SampleModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var modelEntry = Assert.Single(zip.Entries, e => e.Name == "model.xml");

        await using var modelStream = modelEntry.Open();
        var doc = System.Xml.Linq.XDocument.Load(modelStream);

        Assert.Null(doc.Root!.Attribute(TargetVersionAttribute));
    }

    [Fact]
    public async Task Serialize_WithMinorVersion_WritesTheVersionAlongsideDspName()
    {
        var metadata = new ModelMetadata
        {
            ProviderName = "MySql",
            TargetVersion = new TargetVersion(8, 4),
        };

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, SampleModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var modelEntry = Assert.Single(zip.Entries, e => e.Name == "model.xml");

        await using var modelStream = modelEntry.Open();
        var doc = System.Xml.Linq.XDocument.Load(modelStream);

        // The DspName still names the per-major type; the minor rides beside it because that
        // type name has nowhere to put one.
        Assert.Equal(
            "Squill.Provider.MariaDb.MySql8DatabaseSchemaProvider",
            (string?)doc.Root!.Attribute("DspName"));
        Assert.Equal("8.4", (string?)doc.Root!.Attribute(TargetVersionAttribute));
    }

    /// <summary>
    /// The patch is where most of the real thresholds live (MySQL functional index keys at
    /// 8.0.13, enforced CHECK constraints at 8.0.16), so it has to survive the package too — a
    /// floor recorded as 8.0.0 would silently be weaker than the one the author declared.
    /// </summary>
    [Fact]
    public async Task RoundTrip_WithPatchVersion_RestoresAllComponents()
    {
        var metadata = new ModelMetadata
        {
            ProviderName = "MySql",
            TargetVersion = new TargetVersion(8, 0, 13),
        };

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, SampleModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var (result, _) = await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

        Assert.Equal(new TargetVersion(8, 0, 13), result.TargetVersion);
    }

    [Fact]
    public async Task Serialize_WithPatchVersion_WritesTheFullVersion()
    {
        var metadata = new ModelMetadata
        {
            ProviderName = "MySql",
            TargetVersion = new TargetVersion(8, 0, 13),
        };

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, SampleModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var modelEntry = Assert.Single(zip.Entries, e => e.Name == "model.xml");

        await using var modelStream = modelEntry.Open();
        var doc = System.Xml.Linq.XDocument.Load(modelStream);

        Assert.Equal("8.0.13", (string?)doc.Root!.Attribute(TargetVersionAttribute));
    }

    /// <summary>
    /// A package written before the minor attribute existed reads back as <c>.0</c> rather than
    /// failing, so older DACPACs stay deployable.
    /// </summary>
    [Fact]
    public async Task RoundTrip_PackageWithoutTargetVersionAttribute_ReadsAsPointZero()
    {
        var metadata = new ModelMetadata { ProviderName = "Postgresql", TargetMajorVersion = 16 };

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, SampleModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var (result, _) = await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

        Assert.Equal(new TargetVersion(16, 0), result.TargetVersion);
    }
}

using System.IO.Compression;
using System.Text;
using Squill.Core;

namespace Squill.Dacpac.Tests;

/// <summary>
/// Covers the pre/post-deployment script parts of the DACPAC (issue #67). The
/// scripts are optional parts alongside model.xml; a DACPAC with neither is
/// byte-identical to one built before the feature existed, so older packages
/// keep deserializing.
/// </summary>
public class DeploymentScriptSerializationTests
{
    private static Model BuildModel()
    {
        var model = new Model();
        model.Elements.Add(new Element("SqlTable") { Name = "public.Foo" });

        return model;
    }

    [Fact]
    public async Task Serialize_WithNoScripts_OmitsScriptParts()
    {
        var metadata = new ModelMetadata { ProviderName = "Postgresql" };
        await using var stream = new MemoryStream();

        await DacpacSerializer.Serialize(
            metadata, BuildModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(4, zip.Entries.Count);
        Assert.DoesNotContain(zip.Entries, e => e.Name == "predeploy.sql");
        Assert.DoesNotContain(zip.Entries, e => e.Name == "postdeploy.sql");
    }

    [Fact]
    public async Task Serialize_WithScripts_WritesScriptParts()
    {
        var metadata = new ModelMetadata
        {
            ProviderName = "Postgresql",
            PreDeployScript = "SELECT 'before';",
            PostDeployScript = "SELECT 'after';",
        };
        await using var stream = new MemoryStream();

        await DacpacSerializer.Serialize(
            metadata, BuildModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(6, zip.Entries.Count);

        var pre = Assert.Single(zip.Entries, e => e.Name == "predeploy.sql");
        var post = Assert.Single(zip.Entries, e => e.Name == "postdeploy.sql");

        Assert.Equal("SELECT 'before';", await ReadEntryAsync(pre));
        Assert.Equal("SELECT 'after';", await ReadEntryAsync(post));
    }

    [Fact]
    public async Task Serialize_WithOnlyPostDeployScript_OmitsPreDeployPart()
    {
        var metadata = new ModelMetadata
        {
            ProviderName = "Postgresql",
            PostDeployScript = "INSERT INTO foo VALUES (1);",
        };
        await using var stream = new MemoryStream();

        await DacpacSerializer.Serialize(
            metadata, BuildModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.Equal(5, zip.Entries.Count);
        Assert.DoesNotContain(zip.Entries, e => e.Name == "predeploy.sql");
        Assert.Single(zip.Entries, e => e.Name == "postdeploy.sql");
    }

    // SSDT declares the sql Default unconditionally — it is present even in packages
    // containing no .sql part at all — so we match that rather than gating on scripts.
    [Theory]
    [InlineData("")]
    [InlineData("SELECT 1;")]
    public async Task Serialize_AlwaysDeclaresSqlContentType(string postDeployScript)
    {
        var metadata = new ModelMetadata
        {
            ProviderName = "Postgresql",
            PostDeployScript = postDeployScript,
        };
        await using var stream = new MemoryStream();

        await DacpacSerializer.Serialize(
            metadata, BuildModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var contentTypes = Assert.Single(zip.Entries, e => e.Name == "[Content Types].xml");

        var xml = await ReadEntryAsync(contentTypes);

        Assert.Contains("Extension=\"sql\"", xml);
        Assert.Contains("ContentType=\"text/plain\"", xml);
    }

    // DacFx writes the script parts as UTF-8 with a BOM; we match that byte layout.
    [Fact]
    public async Task Serialize_WritesScriptParts_AsUtf8WithBom()
    {
        var metadata = new ModelMetadata
        {
            ProviderName = "Postgresql",
            PostDeployScript = "SELECT 1;",
        };
        await using var stream = new MemoryStream();

        await DacpacSerializer.Serialize(
            metadata, BuildModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var post = Assert.Single(zip.Entries, e => e.Name == "postdeploy.sql");

        await using var entryStream = post.Open();
        using var buffer = new MemoryStream();
        await entryStream.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        var bytes = buffer.ToArray();

        Assert.True(bytes.Length >= 3, "The part should carry a BOM plus content.");
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
    }

    // SSDT records a checksum only for /model.xml, even when script parts are present.
    [Fact]
    public async Task Serialize_DoesNotChecksumScriptParts()
    {
        var metadata = new ModelMetadata
        {
            ProviderName = "Postgresql",
            PreDeployScript = "SELECT 'pre';",
            PostDeployScript = "SELECT 'post';",
        };
        await using var stream = new MemoryStream();

        await DacpacSerializer.Serialize(
            metadata, BuildModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var origin = Assert.Single(zip.Entries, e => e.Name == "Origin.xml");

        var xml = await ReadEntryAsync(origin);

        Assert.Contains("/model.xml", xml);
        Assert.DoesNotContain("predeploy.sql", xml);
        Assert.DoesNotContain("postdeploy.sql", xml);
    }

    // SSDT-built packages carry no _rels/.rels; parts are located by fixed name. Adding
    // one would diverge from the layout we are matching.
    [Fact]
    public async Task Serialize_WritesNoRelationshipsPart()
    {
        var metadata = new ModelMetadata
        {
            ProviderName = "Postgresql",
            PostDeployScript = "SELECT 1;",
        };
        await using var stream = new MemoryStream();

        await DacpacSerializer.Serialize(
            metadata, BuildModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

        Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("_rels"));
    }

    [Fact]
    public async Task RoundTrip_PreservesScripts()
    {
        var metadata = new ModelMetadata
        {
            ProviderName = "Postgresql",
            PreDeployScript = "-- pre\nSELECT 1;",
            PostDeployScript = "-- post\nSELECT 2;",
        };
        await using var stream = new MemoryStream();

        await DacpacSerializer.Serialize(
            metadata, BuildModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var (result, _) = await DacpacSerializer.Deserialize(
            stream, TestContext.Current.CancellationToken);

        Assert.Equal("-- pre\nSELECT 1;", result.PreDeployScript);
        Assert.Equal("-- post\nSELECT 2;", result.PostDeployScript);
    }

    [Fact]
    public async Task RoundTrip_WithNoScripts_YieldsEmptyScripts()
    {
        var metadata = new ModelMetadata { ProviderName = "Postgresql" };
        await using var stream = new MemoryStream();

        await DacpacSerializer.Serialize(
            metadata, BuildModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var (result, _) = await DacpacSerializer.Deserialize(
            stream, TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, result.PreDeployScript);
        Assert.Equal(string.Empty, result.PostDeployScript);
    }

    [Fact]
    public async Task RoundTrip_PreservesNonAsciiScriptContent()
    {
        // Scripts are written as UTF-8 without a BOM; seed data is a common place
        // for non-ASCII text, so verify it survives the round trip intact.
        var metadata = new ModelMetadata
        {
            ProviderName = "Postgresql",
            PostDeployScript = "INSERT INTO city (name) VALUES ('Ōsaka', 'Köln');",
        };
        await using var stream = new MemoryStream();

        await DacpacSerializer.Serialize(
            metadata, BuildModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var (result, _) = await DacpacSerializer.Deserialize(
            stream, TestContext.Current.CancellationToken);

        Assert.Equal("INSERT INTO city (name) VALUES ('Ōsaka', 'Köln');", result.PostDeployScript);
    }

    // A script part written without a BOM (as another tool might) still reads correctly.
    [Fact]
    public async Task Deserialize_ScriptPartWithoutBom_ReadsCleanly()
    {
        var metadata = new ModelMetadata { ProviderName = "Postgresql" };
        await using var stream = new MemoryStream();

        await DacpacSerializer.Serialize(
            metadata, BuildModel(), stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.CreateEntry("postdeploy.sql");
            await using var entryStream = entry.Open();
            await using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
            await writer.WriteAsync("SELECT 1;");
        }

        stream.Position = 0;
        var (result, _) = await DacpacSerializer.Deserialize(
            stream, TestContext.Current.CancellationToken);

        Assert.Equal("SELECT 1;", result.PostDeployScript);
    }

    private static async Task<string> ReadEntryAsync(ZipArchiveEntry entry)
    {
        await using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream, Encoding.UTF8);

        return await reader.ReadToEndAsync();
    }
}

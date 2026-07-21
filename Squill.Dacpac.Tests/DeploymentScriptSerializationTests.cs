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

    [Fact]
    public async Task Serialize_DeclaresSqlContentType_WhenScriptsPresent()
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
        var contentTypes = Assert.Single(zip.Entries, e => e.Name == "[Content Types].xml");

        var xml = await ReadEntryAsync(contentTypes);

        // OPC requires every part extension in the package to be declared.
        Assert.Contains("Extension=\"sql\"", xml);
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

    [Fact]
    public async Task Deserialize_TamperedScript_Throws()
    {
        // Script parts are checksummed in Origin.xml just like model.xml, so a
        // tampered script is caught rather than silently executed on deploy.
        var metadata = new ModelMetadata
        {
            ProviderName = "Postgresql",
            PostDeployScript = "SELECT 1;",
        };
        await using var stream = new MemoryStream();

        await DacpacSerializer.Serialize(
            metadata, BuildModel(), stream, TestContext.Current.CancellationToken);

        // Rewrite postdeploy.sql in place, leaving the recorded checksum stale.
        stream.Position = 0;
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry("postdeploy.sql")!;
            entry.Delete();

            var replacement = zip.CreateEntry("postdeploy.sql");
            await using var entryStream = replacement.Open();
            await using var writer = new StreamWriter(entryStream, new UTF8Encoding(false));
            await writer.WriteAsync("DROP TABLE foo;");
        }

        stream.Position = 0;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken));

        Assert.Contains("postdeploy.sql", exception.Message);
    }

    private static async Task<string> ReadEntryAsync(ZipArchiveEntry entry)
    {
        await using var entryStream = entry.Open();
        using var reader = new StreamReader(entryStream, Encoding.UTF8);

        return await reader.ReadToEndAsync();
    }
}

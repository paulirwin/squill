using System.IO.Compression;
using System.Reflection;
using Squill.Core;

namespace Squill.Dacpac.Tests;

public class DacpacSerializationTests
{
    private static Model BuildSampleModel()
    {
        // A representative model exercising: top-level elements, properties of
        // several value types, Schema/Columns relationships, References (with and
        // without an ExternalSource) and nested Elements with their own
        // relationships/properties.
        var model = new Model();

        var table = new Element("SqlTable") { Name = "public.Foo" };

        var schema = new Relationship("Schema");
        schema.Add(new Reference("public"));
        table.Relationships.Add(schema);

        var columns = new Relationship("Columns");

        var idColumn = new Element("SqlSimpleColumn") { Name = "Foo.id" };
        idColumn.Properties.Add(new Property("IsNullable", false));

        var idType = new Element("SqlTypeSpecifier");
        var idTypeRel = new Relationship("Type");
        idTypeRel.Add(new Reference("integer") { ExternalSource = "BuiltIns" });
        idType.Relationships.Add(idTypeRel);

        var idTypeSpec = new Relationship("TypeSpecifier");
        idTypeSpec.Add(idType);
        idColumn.Relationships.Add(idTypeSpec);

        columns.Add(idColumn);

        var nameColumn = new Element("SqlSimpleColumn") { Name = "Foo.name" };
        nameColumn.Properties.Add(new Property("IsNullable", true));
        nameColumn.Properties.Add(new Property("Length", 100));

        columns.Add(nameColumn);

        table.Relationships.Add(columns);
        model.Elements.Add(table);

        var pk = new Element("SqlPrimaryKeyConstraint") { Name = "Foo.PK" };
        var definingTable = new Relationship("DefiningTable");
        definingTable.Add(new Reference("public.Foo"));
        pk.Relationships.Add(definingTable);
        model.Elements.Add(pk);

        return model;
    }

    [Fact]
    public async Task DacpacSerializer_Serialize_ProducesFourEntries()
    {
        var metadata = new ModelMetadata { ProviderName = "Postgresql" };
        var model = BuildSampleModel();
        await using var stream = new MemoryStream();

        await DacpacSerializer.Serialize(metadata, model, stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Equal(4, zip.Entries.Count);

        Assert.Single(zip.Entries, e => e.Name == "Origin.xml");
        Assert.Single(zip.Entries, e => e.Name == "[Content Types].xml");
        Assert.Single(zip.Entries, e => e.Name == "DacMetadata.xml");
        Assert.Single(zip.Entries, e => e.Name == "model.xml");
    }

    [Fact]
    public async Task DacpacSerializer_Origin_RecordsAssemblyProductVersion()
    {
        var metadata = new ModelMetadata { ProviderName = "Postgresql" };
        var model = BuildSampleModel();
        await using var stream = new MemoryStream();

        await DacpacSerializer.Serialize(metadata, model, stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var originEntry = Assert.Single(zip.Entries, e => e.Name == "Origin.xml");

        await using var originStream = originEntry.Open();
        var doc = System.Xml.Linq.XDocument.Load(originStream);
        var ns = doc.Root!.Name.Namespace;
        var productVersion = doc.Root.Element(ns + "ProductVersion")?.Value;

        // ProductVersion comes from the assembly's version, not a hardcoded value.
        var expected =
            typeof(DacpacSerializer).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion
            ?? typeof(DacpacSerializer).Assembly.GetName().Version?.ToString();

        Assert.False(string.IsNullOrWhiteSpace(productVersion));
        Assert.Equal(expected, productVersion);
    }

    [Fact]
    public async Task DacpacSerializer_RoundTrip_ModelHashMatches()
    {
        var metadata = new ModelMetadata { ProviderName = "Postgresql" };
        var model = BuildSampleModel();
        var originalHash = model.Hash;

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, model, stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var (deserializedMetadata, deserializedModel) =
            await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

        Assert.Equal(metadata.ProviderName, deserializedMetadata.ProviderName);
        Assert.True(
            HashUtility.HashesEqual(originalHash, deserializedModel.Hash),
            "Deserialized model hash must match the original model hash.");
    }

    [Fact]
    public async Task DacpacSerializer_RoundTrip_EmptyModel_HashMatches()
    {
        var metadata = new ModelMetadata { ProviderName = "Postgresql" };
        var model = new Model();
        var originalHash = model.Hash;

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, model, stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var (_, deserializedModel) =
            await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

        Assert.Empty(deserializedModel.Elements);
        Assert.True(HashUtility.HashesEqual(originalHash, deserializedModel.Hash));
    }

    [Fact]
    public async Task DacpacSerializer_RoundTrip_PreservesStructure()
    {
        var metadata = new ModelMetadata { ProviderName = "Postgresql" };
        var model = BuildSampleModel();

        await using var stream = new MemoryStream();
        await DacpacSerializer.Serialize(metadata, model, stream, TestContext.Current.CancellationToken);

        stream.Position = 0;
        var (_, result) = await DacpacSerializer.Deserialize(stream, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Elements.Count);

        var table = result.Elements.Single(e => e.Type == "SqlTable");
        Assert.Equal("public.Foo", table.Name);
        Assert.Equal(2, table.Relationships.Count);

        var columns = table.GetRelationship("Columns")!;
        Assert.Equal(2, columns.Entries.Count);

        var idColumn = Assert.IsType<Element>(columns.Entries[0]);
        Assert.Equal("Foo.id", idColumn.Name);
        Assert.Equal(false, idColumn.GetProperty<object>("IsNullable"));

        var typeSpec = idColumn.GetRelationship("TypeSpecifier")!;
        var typeElement = Assert.IsType<Element>(typeSpec.Entries[0]);
        var typeRef = Assert.IsType<Reference>(typeElement.GetRelationship("Type")!.Entries[0]);
        Assert.Equal("integer", typeRef.Name);
        Assert.Equal("BuiltIns", typeRef.ExternalSource);
    }
}

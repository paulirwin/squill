using System.IO.Compression;
using System.Security.Cryptography;
using Squill.Core;

namespace Squill.Dacpac;

/// <summary>
/// Serializes a <see cref="Model"/> to, and deserializes it from, the DACFx 3.0
/// DACPAC file format — an OPC (Open Packaging Conventions) ZIP archive whose parts
/// are <c>model.xml</c>, <c>Origin.xml</c>, <c>DacMetadata.xml</c> and
/// <c>[Content Types].xml</c>, laid out to mirror an SSDT-built DACPAC. A DACPAC
/// round-trips faithfully: the deserialized model's whole-model hash equals the
/// hash of the model it was built from.
/// </summary>
public static class DacpacSerializer
{
    public static Task Serialize(
        ModelMetadata metadata,
        Model model,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Serialize model.xml to a buffer first so we can checksum its exact bytes
        // and record that checksum in Origin.xml, matching the SSDT layout.
        byte[] modelBytes;
        using (var modelBuffer = new MemoryStream())
        {
            ModelXmlWriter.Write(model, metadata, modelBuffer);
            modelBytes = modelBuffer.ToArray();
        }

        var modelChecksum = Convert.ToHexString(SHA256.HashData(modelBytes));

        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        WriteEntry(zip, DacpacConstants.ContentTypesPart, ContentTypesXml.Write);
        WriteEntry(zip, DacpacConstants.ModelPart, s => s.Write(modelBytes));
        WriteEntry(zip, DacpacConstants.OriginPart, s => OriginXml.Write(metadata, modelChecksum, s));
        WriteEntry(zip, DacpacConstants.DacMetadataPart, s => DacMetadataXml.Write(metadata, s));

        return Task.CompletedTask;
    }

    public static Task<(ModelMetadata, Model)> Deserialize(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        var modelEntry = GetRequiredEntry(zip, DacpacConstants.ModelPart);
        var modelBytes = ReadEntryBytes(modelEntry);

        // Origin.xml carries the provider name; DacMetadata.xml the app name/version.
        // A DACPAC we produced always has both, but tolerate their absence by
        // seeding a placeholder provider that Origin.xml then overwrites.
        var metadata = new ModelMetadata { ProviderName = string.Empty };

        string? recordedChecksum = null;
        if (zip.GetEntry(DacpacConstants.OriginPart) is { } originEntry)
        {
            using var originStream = originEntry.Open();
            recordedChecksum = OriginXml.ReadInto(originStream, metadata);
        }

        if (zip.GetEntry(DacpacConstants.DacMetadataPart) is { } dacMetadataEntry)
        {
            using var dacMetadataStream = dacMetadataEntry.Open();
            DacMetadataXml.ReadInto(dacMetadataStream, metadata);
        }

        // Verify the model part against the checksum recorded in Origin.xml, so a
        // corrupt or tampered model.xml is caught rather than silently deserialized.
        if (recordedChecksum is not null)
        {
            var actualChecksum = Convert.ToHexString(SHA256.HashData(modelBytes));
            if (!string.Equals(actualChecksum, recordedChecksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "model.xml checksum does not match the value recorded in Origin.xml; "
                    + "the DACPAC may be corrupt.");
            }
        }

        using var modelStream = new MemoryStream(modelBytes, writable: false);
        var model = ModelXmlReader.Read(modelStream, metadata);

        return Task.FromResult((metadata, model));
    }

    private static void WriteEntry(ZipArchive zip, string name, Action<Stream> write)
    {
        var entry = zip.CreateEntry(name);
        using var entryStream = entry.Open();
        write(entryStream);
    }

    private static ZipArchiveEntry GetRequiredEntry(ZipArchive zip, string name)
        => zip.GetEntry(name)
           ?? throw new InvalidOperationException($"DACPAC is missing the required '{name}' part.");

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var entryStream = entry.Open();
        using var buffer = new MemoryStream();
        entryStream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

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
    // DacFx writes the deploy-script parts as UTF-8 *with* a BOM (verified by inspecting
    // SSDT-built packages), so we match that for byte-compatible output. The BOM is
    // stripped on read so the script text itself round-trips unchanged.
    private static readonly System.Text.UTF8Encoding ScriptEncoding =
        new(encoderShouldEmitUTF8Identifier: true);

    // U+FEFF, the character a UTF-8 BOM decodes to.
    private const char ByteOrderMark = '﻿';

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

        // Only model.xml is checksummed. SSDT-built packages record no checksum for the
        // deploy-script parts even when present, so neither do we.
        var modelChecksum = Convert.ToHexString(SHA256.HashData(modelBytes));

        // Pre/post-deployment scripts are optional root-level parts, named exactly as
        // DacFx names them.
        var preDeployBytes = EncodeScript(metadata.PreDeployScript);
        var postDeployBytes = EncodeScript(metadata.PostDeployScript);

        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        WriteEntry(zip, DacpacConstants.ContentTypesPart, ContentTypesXml.Write);
        WriteEntry(zip, DacpacConstants.ModelPart, s => s.Write(modelBytes));
        WriteEntry(zip, DacpacConstants.OriginPart, s => OriginXml.Write(metadata, modelChecksum, s));
        WriteEntry(zip, DacpacConstants.DacMetadataPart, s => DacMetadataXml.Write(metadata, s));

        if (preDeployBytes is not null)
        {
            WriteEntry(zip, DacpacConstants.PreDeployPart, s => s.Write(preDeployBytes));
        }

        if (postDeployBytes is not null)
        {
            WriteEntry(zip, DacpacConstants.PostDeployPart, s => s.Write(postDeployBytes));
        }

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

        metadata.PreDeployScript = ReadScriptPart(zip, DacpacConstants.PreDeployPart);
        metadata.PostDeployScript = ReadScriptPart(zip, DacpacConstants.PostDeployPart);

        using var modelStream = new MemoryStream(modelBytes, writable: false);
        var model = ModelXmlReader.Read(modelStream, metadata);

        return Task.FromResult((metadata, model));
    }

    /// <summary>
    /// Encodes a deploy script as the bytes of its part — UTF-8 with a BOM, as DacFx
    /// writes them — or <c>null</c> when there is no script, in which case the part is
    /// omitted entirely.
    /// </summary>
    private static byte[]? EncodeScript(string script)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            return null;
        }

        var preamble = ScriptEncoding.GetPreamble();
        var bytes = new byte[preamble.Length + ScriptEncoding.GetByteCount(script)];

        preamble.CopyTo(bytes, 0);
        ScriptEncoding.GetBytes(script, 0, script.Length, bytes, preamble.Length);

        return bytes;
    }

    /// <summary>
    /// Reads an optional deploy-script part, stripping the leading BOM so the script text
    /// round-trips unchanged. Returns an empty string when the part is absent.
    /// </summary>
    private static string ReadScriptPart(ZipArchive zip, string partName)
    {
        if (zip.GetEntry(partName) is not { } entry)
        {
            return string.Empty;
        }

        // A leading BOM is part of the encoding, not of the script: leaving it in place
        // would put U+FEFF at the head of the SQL we send to the database.
        var text = ScriptEncoding.GetString(ReadEntryBytes(entry));

        return text.TrimStart(ByteOrderMark);
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

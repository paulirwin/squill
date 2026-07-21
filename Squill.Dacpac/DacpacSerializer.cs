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
    // Deploy-script parts are UTF-8 without a BOM, so the script text round-trips
    // byte-for-byte and executes as written against the target database.
    private static readonly System.Text.UTF8Encoding ScriptEncoding =
        new(encoderShouldEmitUTF8Identifier: false);

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

        var checksums = new List<(string Uri, string Checksum)>
        {
            (DacpacConstants.ModelPartUri, Convert.ToHexString(SHA256.HashData(modelBytes))),
        };

        // Pre/post-deployment scripts are optional parts, written as UTF-8 without a BOM
        // and checksummed alongside model.xml so tampering is detected before they run.
        var preDeployBytes = EncodeScript(metadata.PreDeployScript);
        var postDeployBytes = EncodeScript(metadata.PostDeployScript);

        if (preDeployBytes is not null)
        {
            checksums.Add((
                DacpacConstants.PreDeployPartUri,
                Convert.ToHexString(SHA256.HashData(preDeployBytes))));
        }

        if (postDeployBytes is not null)
        {
            checksums.Add((
                DacpacConstants.PostDeployPartUri,
                Convert.ToHexString(SHA256.HashData(postDeployBytes))));
        }

        var hasScripts = preDeployBytes is not null || postDeployBytes is not null;

        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        WriteEntry(zip, DacpacConstants.ContentTypesPart, s => ContentTypesXml.Write(s, hasScripts));
        WriteEntry(zip, DacpacConstants.ModelPart, s => s.Write(modelBytes));
        WriteEntry(zip, DacpacConstants.OriginPart, s => OriginXml.Write(metadata, checksums, s));
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

        IReadOnlyDictionary<string, string> recordedChecksums =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (zip.GetEntry(DacpacConstants.OriginPart) is { } originEntry)
        {
            using var originStream = originEntry.Open();
            recordedChecksums = OriginXml.ReadInto(originStream, metadata);
        }

        if (zip.GetEntry(DacpacConstants.DacMetadataPart) is { } dacMetadataEntry)
        {
            using var dacMetadataStream = dacMetadataEntry.Open();
            DacMetadataXml.ReadInto(dacMetadataStream, metadata);
        }

        // Verify each part against the checksum recorded in Origin.xml, so a corrupt or
        // tampered part is caught rather than silently deserialized — or, for the deploy
        // scripts, silently executed against the target database.
        VerifyChecksum(recordedChecksums, DacpacConstants.ModelPartUri, DacpacConstants.ModelPart, modelBytes);

        metadata.PreDeployScript = ReadScriptPart(
            zip, DacpacConstants.PreDeployPart, DacpacConstants.PreDeployPartUri, recordedChecksums);
        metadata.PostDeployScript = ReadScriptPart(
            zip, DacpacConstants.PostDeployPart, DacpacConstants.PostDeployPartUri, recordedChecksums);

        using var modelStream = new MemoryStream(modelBytes, writable: false);
        var model = ModelXmlReader.Read(modelStream, metadata);

        return Task.FromResult((metadata, model));
    }

    /// <summary>
    /// Encodes a deploy script as the UTF-8 (no BOM) bytes of its part, or <c>null</c>
    /// when there is no script — in which case the part is omitted entirely.
    /// </summary>
    private static byte[]? EncodeScript(string script)
        => string.IsNullOrWhiteSpace(script) ? null : ScriptEncoding.GetBytes(script);

    /// <summary>
    /// Reads an optional deploy-script part, verifying it against its recorded checksum.
    /// Returns an empty string when the part is absent.
    /// </summary>
    private static string ReadScriptPart(
        ZipArchive zip,
        string partName,
        string partUri,
        IReadOnlyDictionary<string, string> recordedChecksums)
    {
        if (zip.GetEntry(partName) is not { } entry)
        {
            return string.Empty;
        }

        var bytes = ReadEntryBytes(entry);
        VerifyChecksum(recordedChecksums, partUri, partName, bytes);

        return ScriptEncoding.GetString(bytes);
    }

    private static void VerifyChecksum(
        IReadOnlyDictionary<string, string> recordedChecksums,
        string partUri,
        string partName,
        byte[] bytes)
    {
        // A DACPAC we produced always records a checksum for every part it contains;
        // tolerate its absence rather than rejecting a package written by another tool.
        if (!recordedChecksums.TryGetValue(partUri, out var recorded))
        {
            return;
        }

        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(actual, recorded, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{partName} checksum does not match the value recorded in Origin.xml; "
                + "the DACPAC may be corrupt.");
        }
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

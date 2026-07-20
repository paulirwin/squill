using System.Xml;
using System.Xml.Linq;

namespace Squill.Dacpac;

/// <summary>
/// Reads and writes the DACPAC <c>Origin.xml</c> part. In the SSDT format this
/// records provenance for the package (the producing tool and version, the
/// database schema provider name, and a checksum over <c>model.xml</c>). The
/// checksum lets a consumer detect a tampered or corrupt model part.
/// </summary>
internal static class OriginXml
{
    public static void Write(ModelMetadata metadata, string modelChecksum, Stream stream)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CloseOutput = false,
        };

        using var writer = XmlWriter.Create(stream, settings);

        writer.WriteStartDocument();
        writer.WriteStartElement("DacOrigin", DacpacConstants.SerializationNamespace);

        writer.WriteElementString("ProductName", DacpacConstants.SerializationNamespace, DacpacConstants.ProductName);
        writer.WriteElementString("ProductVersion", DacpacConstants.SerializationNamespace, DacpacConstants.ProductVersion);

        // DspName identifies the database schema provider so the deploy side knows
        // which IDatabaseProvider to use when consuming this DACPAC.
        writer.WriteElementString("DspName", DacpacConstants.SerializationNamespace, metadata.ProviderName);

        writer.WriteStartElement("Checksums", DacpacConstants.SerializationNamespace);
        writer.WriteStartElement("Checksum", DacpacConstants.SerializationNamespace);
        writer.WriteAttributeString("Uri", DacpacConstants.ModelPartUri);
        writer.WriteString(modelChecksum);
        writer.WriteEndElement(); // Checksum
        writer.WriteEndElement(); // Checksums

        writer.WriteEndElement(); // DacOrigin
        writer.WriteEndDocument();
        writer.Flush();
    }

    /// <summary>
    /// Reads Origin.xml, applying the provider name onto the metadata and returning
    /// the recorded checksum for <c>model.xml</c> (null if none is present).
    /// </summary>
    public static string? ReadInto(Stream stream, ModelMetadata metadata)
    {
        var document = XDocument.Load(stream);
        var root = document.Root
                   ?? throw new InvalidOperationException("Origin.xml has no root element.");
        var ns = root.Name.Namespace;

        if (root.Element(ns + "DspName")?.Value is { } dspName)
        {
            metadata.ProviderName = dspName;
        }

        return root.Element(ns + "Checksums")?
            .Elements(ns + "Checksum")
            .FirstOrDefault(c => (string?)c.Attribute("Uri") == DacpacConstants.ModelPartUri)?
            .Value;
    }
}

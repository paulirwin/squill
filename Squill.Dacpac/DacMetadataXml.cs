using System.Xml;
using System.Xml.Linq;

namespace Squill.Dacpac;

/// <summary>
/// Reads and writes the DACPAC <c>DacMetadata.xml</c> part, which records the name
/// and version of the data-tier application (the SSDT <c>DacType</c> element).
/// </summary>
internal static class DacMetadataXml
{
    public static void Write(ModelMetadata metadata, Stream stream)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CloseOutput = false,
        };

        using var writer = XmlWriter.Create(stream, settings);

        writer.WriteStartDocument();
        writer.WriteStartElement("DacType", DacpacConstants.SerializationNamespace);
        writer.WriteElementString("Name", DacpacConstants.SerializationNamespace, metadata.Name);
        writer.WriteElementString("Version", DacpacConstants.SerializationNamespace, metadata.Version);
        writer.WriteElementString("Description", DacpacConstants.SerializationNamespace, metadata.Description);
        writer.WriteEndElement(); // DacType
        writer.WriteEndDocument();
        writer.Flush();
    }

    /// <summary>
    /// Reads the Name/Version/Description back onto the given metadata instance.
    /// </summary>
    public static void ReadInto(Stream stream, ModelMetadata metadata)
    {
        var document = XDocument.Load(stream);
        var root = document.Root
                   ?? throw new InvalidOperationException("DacMetadata.xml has no root element.");
        var ns = root.Name.Namespace;

        if (root.Element(ns + "Name")?.Value is { } name)
        {
            metadata.Name = name;
        }

        if (root.Element(ns + "Version")?.Value is { } version)
        {
            metadata.Version = version;
        }

        if (root.Element(ns + "Description")?.Value is { } description)
        {
            metadata.Description = description;
        }
    }
}

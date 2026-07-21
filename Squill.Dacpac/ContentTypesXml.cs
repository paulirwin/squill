using System.Xml;

namespace Squill.Dacpac;

/// <summary>
/// Writes the OPC <c>[Content Types].xml</c> part that every DACPAC (an Open
/// Packaging Conventions ZIP) must carry. It declares the content type for the
/// XML parts in the package.
/// </summary>
internal static class ContentTypesXml
{
    private const string ContentTypesNamespace =
        "http://schemas.openxmlformats.org/package/2006/content-types";

    public static void Write(Stream stream)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CloseOutput = false,
        };

        using var writer = XmlWriter.Create(stream, settings);

        writer.WriteStartDocument();
        writer.WriteStartElement("Types", ContentTypesNamespace);

        // The model/metadata parts are XML.
        writer.WriteStartElement("Default", ContentTypesNamespace);
        writer.WriteAttributeString("Extension", "xml");
        writer.WriteAttributeString("ContentType", "text/xml");
        writer.WriteEndElement(); // Default

        // Pre/post-deployment scripts are plain-text SQL parts. SSDT declares this
        // unconditionally — even in packages containing no .sql part at all — so we
        // match that rather than gating it on whether scripts are present.
        writer.WriteStartElement("Default", ContentTypesNamespace);
        writer.WriteAttributeString("Extension", "sql");
        writer.WriteAttributeString("ContentType", "text/plain");
        writer.WriteEndElement(); // Default

        writer.WriteEndElement(); // Types
        writer.WriteEndDocument();
        writer.Flush();
    }
}

using System.Globalization;
using System.Xml;
using Squill.Core;

namespace Squill.Dacpac;

/// <summary>
/// Serializes a <see cref="Model"/> to the DACFx 3.0 <c>model.xml</c> part.
/// The layout mirrors an SSDT-built DACPAC: a <c>DataSchemaModel</c> root
/// containing a <c>Model</c> of <c>Element</c> nodes, each carrying
/// <c>Property</c>, <c>Relationship</c> (with <c>Entry</c> wrappers around a
/// nested <c>Element</c> or a <c>References</c>) and <c>Annotation</c> children.
/// </summary>
internal static class ModelXmlWriter
{
    public static void Write(Model model, Stream stream)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            Encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CloseOutput = false,
        };

        using var writer = XmlWriter.Create(stream, settings);

        writer.WriteStartDocument();
        writer.WriteStartElement("DataSchemaModel", DacpacConstants.SerializationNamespace);
        writer.WriteAttributeString("FileFormatVersion", DacpacConstants.FileFormatVersion);
        writer.WriteAttributeString("SchemaVersion", DacpacConstants.SchemaVersion);

        writer.WriteStartElement("Model");

        foreach (var element in model.Elements)
        {
            WriteElement(writer, element);
        }

        writer.WriteEndElement(); // Model
        writer.WriteEndElement(); // DataSchemaModel
        writer.WriteEndDocument();
        writer.Flush();
    }

    private static void WriteElement(XmlWriter writer, Element element)
    {
        writer.WriteStartElement("Element");
        writer.WriteAttributeString("Type", element.Type);
        if (element.Name is not null)
        {
            writer.WriteAttributeString("Name", element.Name);
        }

        foreach (var property in element.Properties)
        {
            WriteProperty(writer, property);
        }

        foreach (var relationship in element.Relationships)
        {
            WriteRelationship(writer, relationship);
        }

        foreach (var annotation in element.Annotations)
        {
            WriteAnnotation(writer, annotation);
        }

        writer.WriteEndElement(); // Element
    }

    private static void WriteProperty(XmlWriter writer, Property property)
    {
        writer.WriteStartElement("Property");
        writer.WriteAttributeString("Name", property.Name);

        var (value, valueType) = EncodeValue(property.Value);
        if (value is not null)
        {
            writer.WriteAttributeString("Value", value);
        }

        // A non-string type hint so primitive values round-trip to their original
        // CLR type. The whole-model hash only depends on Value.ToString(), so this
        // never affects hash equality, but it keeps the deserialized model faithful.
        if (valueType is not null)
        {
            writer.WriteAttributeString("ValueType", valueType);
        }

        writer.WriteEndElement(); // Property
    }

    private static void WriteRelationship(XmlWriter writer, Relationship relationship)
    {
        writer.WriteStartElement("Relationship");
        writer.WriteAttributeString("Name", relationship.Name);

        foreach (var entry in relationship.Entries)
        {
            writer.WriteStartElement("Entry");

            switch (entry)
            {
                case Element nestedElement:
                    WriteElement(writer, nestedElement);
                    break;
                case Reference reference:
                    writer.WriteStartElement("References");
                    writer.WriteAttributeString("Name", reference.Name);
                    if (reference.ExternalSource is not null)
                    {
                        writer.WriteAttributeString("ExternalSource", reference.ExternalSource);
                    }

                    writer.WriteEndElement(); // References
                    break;
                default:
                    throw new NotSupportedException(
                        $"Unsupported relationship entry type: {entry.GetType().FullName}");
            }

            writer.WriteEndElement(); // Entry
        }

        writer.WriteEndElement(); // Relationship
    }

    private static void WriteAnnotation(XmlWriter writer, Annotation annotation)
    {
        writer.WriteStartElement("Annotation");
        writer.WriteAttributeString("Type", annotation.Type);
        if (annotation.Disambiguator is { } disambiguator)
        {
            writer.WriteAttributeString(
                "Disambiguator",
                disambiguator.ToString(CultureInfo.InvariantCulture));
        }

        writer.WriteEndElement(); // Annotation
    }

    /// <summary>
    /// Encodes a property value to its invariant string form plus an optional type
    /// hint. Returns (null, null) for a null value.
    /// </summary>
    private static (string? Value, string? ValueType) EncodeValue(object? value)
    {
        return value switch
        {
            null => (null, null),
            string s => (s, null),
            bool b => (b ? "true" : "false", "Boolean"),
            int i => (i.ToString(CultureInfo.InvariantCulture), "Int32"),
            long l => (l.ToString(CultureInfo.InvariantCulture), "Int64"),
            // Fall back to the invariant ToString for any other type. The hash uses
            // ToString too, so this remains hash-faithful even without a type hint.
            IFormattable f => (f.ToString(null, CultureInfo.InvariantCulture), null),
            _ => (value.ToString(), null),
        };
    }
}

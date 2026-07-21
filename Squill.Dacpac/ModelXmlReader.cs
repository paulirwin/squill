using System.Globalization;
using System.Xml.Linq;
using Squill.Core;

namespace Squill.Dacpac;

/// <summary>
/// Deserializes the DACFx 3.0 <c>model.xml</c> part produced by
/// <see cref="ModelXmlWriter"/> back into a <see cref="Model"/>. The reconstructed
/// model preserves element type/name, properties (with their original CLR type
/// where a hint is present), relationships and their entries (nested elements and
/// references), and annotations — so the deserialized model's whole-model hash
/// matches the original.
/// </summary>
internal static class ModelXmlReader
{
    public static Model Read(Stream stream, ModelMetadata? metadata = null)
    {
        var document = XDocument.Load(stream);
        var root = document.Root
                   ?? throw new InvalidOperationException("model.xml has no root element.");

        var ns = root.Name.Namespace;
        var model = new Model();

        // The DspName on the root records the target engine major version (SSDT-style).
        // Apply it onto the metadata so the deploy side can enforce it.
        if (metadata is not null
            && DspName.TryParse((string?)root.Attribute("DspName"), out _, out var targetMajorVersion))
        {
            metadata.TargetMajorVersion = targetMajorVersion;
        }

        var modelElement = root.Element(ns + "Model");
        if (modelElement is null)
        {
            return model;
        }

        foreach (var elementXml in modelElement.Elements(ns + "Element"))
        {
            model.Elements.Add(ReadElement(elementXml, ns));
        }

        return model;
    }

    private static Element ReadElement(XElement xml, XNamespace ns)
    {
        var type = (string?)xml.Attribute("Type")
                   ?? throw new InvalidOperationException("Element is missing its Type attribute.");

        var element = new Element(type)
        {
            Name = (string?)xml.Attribute("Name"),
        };

        foreach (var child in xml.Elements())
        {
            if (child.Name == ns + "Property")
            {
                element.Properties.Add(ReadProperty(child));
            }
            else if (child.Name == ns + "Relationship")
            {
                element.Relationships.Add(ReadRelationship(child, ns));
            }
            else if (child.Name == ns + "Annotation")
            {
                element.Annotations.Add(ReadAnnotation(child));
            }
        }

        return element;
    }

    private static Property ReadProperty(XElement xml)
    {
        var name = (string?)xml.Attribute("Name")
                   ?? throw new InvalidOperationException("Property is missing its Name attribute.");

        var rawValue = (string?)xml.Attribute("Value");
        var valueType = (string?)xml.Attribute("ValueType");

        return new Property(name, DecodeValue(rawValue, valueType));
    }

    private static Relationship ReadRelationship(XElement xml, XNamespace ns)
    {
        var name = (string?)xml.Attribute("Name")
                   ?? throw new InvalidOperationException("Relationship is missing its Name attribute.");

        var relationship = new Relationship(name);

        foreach (var entryXml in xml.Elements(ns + "Entry"))
        {
            var nestedElement = entryXml.Element(ns + "Element");
            if (nestedElement is not null)
            {
                relationship.Add(ReadElement(nestedElement, ns));
                continue;
            }

            var referenceXml = entryXml.Element(ns + "References");
            if (referenceXml is not null)
            {
                var refName = (string?)referenceXml.Attribute("Name")
                              ?? throw new InvalidOperationException(
                                  "References is missing its Name attribute.");
                relationship.Add(new Reference(refName)
                {
                    ExternalSource = (string?)referenceXml.Attribute("ExternalSource"),
                });
            }
        }

        return relationship;
    }

    private static Annotation ReadAnnotation(XElement xml)
    {
        var type = (string?)xml.Attribute("Type")
                   ?? throw new InvalidOperationException("Annotation is missing its Type attribute.");

        var annotation = new Annotation(type);

        if ((string?)xml.Attribute("Disambiguator") is { } disambiguator)
        {
            annotation.Disambiguator = int.Parse(disambiguator, CultureInfo.InvariantCulture);
        }

        return annotation;
    }

    private static object? DecodeValue(string? rawValue, string? valueType)
    {
        if (rawValue is null)
        {
            return null;
        }

        return valueType switch
        {
            "Boolean" => bool.Parse(rawValue),
            "Int32" => int.Parse(rawValue, CultureInfo.InvariantCulture),
            "Int64" => long.Parse(rawValue, CultureInfo.InvariantCulture),
            _ => rawValue,
        };
    }
}

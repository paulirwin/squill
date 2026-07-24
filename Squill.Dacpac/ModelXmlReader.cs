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
///
/// <para>
/// One facet is not carried in the XML and is restored from the provider instead:
/// <see cref="Property.ParticipatesInIdentity"/>. It has no SSDT-compatible representation in
/// <c>model.xml</c>, but it is a static rule of the element type rather than per-model data, so
/// the caller's <see cref="IModelIdentityRules"/> (when supplied) re-applies it on load. Without
/// that, a property that opted out of its element's identity would come back participating and
/// the element could never hash-match its deployed counterpart (issue #122).
/// </para>
/// </summary>
internal static class ModelXmlReader
{
    public static Model Read(
        Stream stream, ModelMetadata? metadata = null, IModelIdentityRules? identityRules = null)
    {
        var document = XDocument.Load(stream);
        var root = document.Root
                   ?? throw new InvalidOperationException("model.xml has no root element.");

        var ns = root.Name.Namespace;
        var model = new Model();

        // The DspName on the root names the target-platform schema-provider type (SSDT-style).
        // Resolve it back to a supported provider and apply its major version onto the metadata
        // so the deploy side can enforce it. An unsupported/unknown DspName throws here.
        if (metadata is not null && (string?)root.Attribute("DspName") is { } dspName)
        {
            var schemaProvider = DatabaseSchemaProviderRegistry.ResolveByDspName(dspName);
            metadata.TargetMajorVersion = schemaProvider.MajorVersion;
        }

        var modelElement = root.Element(ns + "Model");
        if (modelElement is null)
        {
            return model;
        }

        foreach (var elementXml in modelElement.Elements(ns + "Element"))
        {
            model.Elements.Add(ReadElement(elementXml, ns, identityRules));
        }

        return model;
    }

    private static Element ReadElement(XElement xml, XNamespace ns, IModelIdentityRules? identityRules)
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
                element.Properties.Add(ReadProperty(child, type, identityRules));
            }
            else if (child.Name == ns + "Relationship")
            {
                element.Relationships.Add(ReadRelationship(child, ns, identityRules));
            }
            else if (child.Name == ns + "Annotation")
            {
                element.Annotations.Add(ReadAnnotation(child));
            }
        }

        return element;
    }

    private static Property ReadProperty(
        XElement xml, string elementType, IModelIdentityRules? identityRules)
    {
        var name = (string?)xml.Attribute("Name")
                   ?? throw new InvalidOperationException("Property is missing its Name attribute.");

        var rawValue = (string?)xml.Attribute("Value");
        var valueType = (string?)xml.Attribute("ValueType");

        // Not stored in the XML — restated by the provider. With no rules the default stands
        // and the property participates, as every property did before issue #122.
        var participatesInIdentity =
            identityRules?.ParticipatesInIdentity(elementType, name) ?? true;

        return new Property(name, DecodeValue(rawValue, valueType), participatesInIdentity);
    }

    private static Relationship ReadRelationship(
        XElement xml, XNamespace ns, IModelIdentityRules? identityRules)
    {
        var name = (string?)xml.Attribute("Name")
                   ?? throw new InvalidOperationException("Relationship is missing its Name attribute.");

        var relationship = new Relationship(name);

        foreach (var entryXml in xml.Elements(ns + "Entry"))
        {
            var nestedElement = entryXml.Element(ns + "Element");
            if (nestedElement is not null)
            {
                relationship.Add(ReadElement(nestedElement, ns, identityRules));
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

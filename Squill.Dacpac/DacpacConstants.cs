namespace Squill.Dacpac;

/// <summary>
/// XML names, namespaces and part names used by the DACFx 3.0 DACPAC file format.
/// A DACPAC is an OPC (Open Packaging Conventions) ZIP archive whose parts are
/// documented by [MS-DACPAC]. We mirror that layout so our output is shaped like
/// an SSDT-built DACPAC. See
/// https://learn.microsoft.com/en-us/openspecs/sql_data_portability/ms-dacpac/
/// </summary>
internal static class DacpacConstants
{
    // Part (zip entry) names.
    public const string ModelPart = "model.xml";
    public const string OriginPart = "Origin.xml";
    public const string DacMetadataPart = "DacMetadata.xml";
    public const string ContentTypesPart = "[Content Types].xml";

    // The Uri form used to reference the model part from Origin.xml checksums.
    public const string ModelPartUri = "/model.xml";

    // DACFx 3.0 serialization namespace, shared by model.xml, Origin.xml and
    // DacMetadata.xml.
    public const string SerializationNamespace =
        "http://schemas.microsoft.com/sqlserver/dac/Serialization/2012/02";

    // Values SSDT writes into the DataSchemaModel root element.
    public const string FileFormatVersion = "1.2";
    public const string SchemaVersion = "3.0";

    // The name of the tool producing the package, recorded in Origin.xml. The
    // accompanying ProductVersion is read from the assembly at write time.
    public const string ProductName = "Squill";
}

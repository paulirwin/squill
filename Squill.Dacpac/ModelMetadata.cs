namespace Squill.Dacpac;

public class ModelMetadata
{
    // TODO.JB - All metadata necessary to support determining database provider details
    public required string ProviderName { get; set; }

    /// <summary>
    /// The name of the data-tier application this DACPAC represents. Written to
    /// DacMetadata.xml (the SSDT DACPAC's DacType/Name element).
    /// </summary>
    public string Name { get; set; } = "Squill";

    /// <summary>
    /// The version of the data-tier application. Written to DacMetadata.xml.
    /// </summary>
    public string Version { get; set; } = "1.0.0.0";

    /// <summary>
    /// An optional human-readable description of the data-tier application.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

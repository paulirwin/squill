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

    /// <summary>
    /// The minimum target database engine <em>major</em> version this DACPAC was built for
    /// (e.g. <c>16</c> for PostgreSQL, <c>11</c> for MariaDB), like SSDT's target platform.
    /// Encoded into the DSP name on the <c>model.xml</c> root (see <see cref="DspName"/>) and
    /// checked at deploy time: if the target server's major version is lower than this, the
    /// deploy fails. <c>null</c> means no version constraint.
    /// </summary>
    public int? TargetMajorVersion { get; set; }
}

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
    /// SQL run against the target database <em>before</em> the schema diff is applied,
    /// stored as the DACPAC's <c>predeploy.sql</c> part. Empty when the project declares
    /// no pre-deployment script. Unlike the declarative sources this is imperative script
    /// text: it is never parsed into the model, and it runs verbatim on every deploy.
    /// </summary>
    public string PreDeployScript { get; set; } = string.Empty;

    /// <summary>
    /// SQL run against the target database <em>after</em> the schema diff is applied,
    /// stored as the DACPAC's <c>postdeploy.sql</c> part. The usual home for seeding or
    /// data-preparation statements, which should be written to be idempotent since they
    /// run on every deploy. See <see cref="PreDeployScript"/>.
    /// </summary>
    public string PostDeployScript { get; set; } = string.Empty;

    /// <summary>
    /// The minimum target database engine version this DACPAC was built for (e.g. <c>16.0</c>
    /// for PostgreSQL, <c>8.4</c> for MySQL), like SSDT's target platform. It is a
    /// <em>floor</em>: the deploy fails when the target server is older, and no ceiling is
    /// implied, so any newer server is accepted. <c>null</c> means no version constraint.
    ///
    /// <para>
    /// The major is encoded into the DSP name on the <c>model.xml</c> root, which names one type
    /// per major and so cannot carry anything below it; the full version is written alongside as
    /// its own attribute. See <see cref="Squill.Dacpac.TargetVersion"/> for the floor semantics.
    /// </para>
    /// </summary>
    public TargetVersion? TargetVersion { get; set; }

    /// <summary>
    /// The major component of <see cref="TargetVersion"/>, or <c>null</c> when unconstrained.
    /// Reading it is useful because the DSP name and the schema-provider registry are keyed on
    /// the major alone. Assigning a major sets a floor of <c>major.0.0</c>, which is what naming
    /// a bare major means anyway — so this stays a faithful spelling of the same thing rather
    /// than a lossy shortcut.
    /// </summary>
    public int? TargetMajorVersion
    {
        get => TargetVersion?.Major;
        set => TargetVersion = value is { } major ? new TargetVersion(major, 0, 0) : null;
    }
}

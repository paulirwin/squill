namespace Squill.Core;

/// <summary>
/// Thrown before any SQL is executed when the target database engine's version is lower than the
/// version the DACPAC was built for (its recorded target platform). Mirrors SSDT, which blocks a
/// publish whose target platform is newer than the server being deployed to, so a DACPAC is never
/// deployed to a server that predates the version it was authored against.
///
/// <para>
/// The comparison carries a minor as well as a major because much of the MySQL and MariaDB DDL
/// surface arrived in point releases: an 8.4-targeting package genuinely cannot deploy to 8.0,
/// even though both are major 8. The recorded target is a floor with no upper bound, so a server
/// newer than it never trips this.
/// </para>
/// </summary>
public class TargetVersionMismatchException : Exception
{
    /// <param name="requiredVersion">
    /// The recorded target rendered for display (e.g. <c>8.0.13</c>). Passed in rather than
    /// rebuilt from the components so the message shows the version the author actually declared,
    /// including a patch, which the numeric properties below do not carry.
    /// </param>
    /// <param name="actualVersion">The connected server's version, rendered for display.</param>
    public TargetVersionMismatchException(
        int requiredMajorVersion,
        int requiredMinorVersion,
        int actualMajorVersion,
        int actualMinorVersion,
        string engineName,
        string requiredVersion,
        string actualVersion)
        : base($"This DACPAC targets {engineName} {requiredVersion} or later, but the target "
               + $"server is {engineName} {actualVersion}. Deploy to a {engineName} "
               + $"{requiredVersion}+ server, or rebuild the DACPAC with a lower "
               + "SquillTargetVersion.")
    {
        RequiredMajorVersion = requiredMajorVersion;
        RequiredMinorVersion = requiredMinorVersion;
        ActualMajorVersion = actualMajorVersion;
        ActualMinorVersion = actualMinorVersion;
        EngineName = engineName;
        RequiredVersion = requiredVersion;
        ActualVersion = actualVersion;
    }

    /// <summary>The minimum engine major version recorded in the DACPAC.</summary>
    public int RequiredMajorVersion { get; }

    /// <summary>The minor component of the minimum engine version recorded in the DACPAC.</summary>
    public int RequiredMinorVersion { get; }

    /// <summary>The target server's actual engine major version.</summary>
    public int ActualMajorVersion { get; }

    /// <summary>The minor component of the target server's actual engine version.</summary>
    public int ActualMinorVersion { get; }

    /// <summary>The engine's display name (e.g. <c>PostgreSQL</c>, <c>MariaDB</c>).</summary>
    public string EngineName { get; }

    /// <summary>
    /// The recorded target as declared (e.g. <c>8.0.13</c>). Carries the patch component, which
    /// <see cref="RequiredMajorVersion"/> and <see cref="RequiredMinorVersion"/> do not.
    /// </summary>
    public string RequiredVersion { get; }

    /// <summary>The connected server's version as reported (e.g. <c>8.0.36</c>).</summary>
    public string ActualVersion { get; }
}

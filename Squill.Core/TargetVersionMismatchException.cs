namespace Squill.Core;

/// <summary>
/// Thrown before any SQL is executed when the target database engine's major version is lower
/// than the version the DACPAC was built for (its recorded target platform). Mirrors SSDT,
/// which blocks a publish whose target platform is newer than the server being deployed to, so
/// a DACPAC is never deployed to a server that predates the version it was authored against.
/// </summary>
public class TargetVersionMismatchException : Exception
{
    public TargetVersionMismatchException(int requiredMajorVersion, int actualMajorVersion, string engineName)
        : base($"This DACPAC targets {engineName} {requiredMajorVersion} or later, but the target "
               + $"server is {engineName} {actualMajorVersion}. Deploy to a {engineName} "
               + $"{requiredMajorVersion}+ server, or rebuild the DACPAC with a lower "
               + "SquillTargetVersion.")
    {
        RequiredMajorVersion = requiredMajorVersion;
        ActualMajorVersion = actualMajorVersion;
        EngineName = engineName;
    }

    /// <summary>The minimum engine major version recorded in the DACPAC.</summary>
    public int RequiredMajorVersion { get; }

    /// <summary>The target server's actual engine major version.</summary>
    public int ActualMajorVersion { get; }

    /// <summary>The engine's display name (e.g. <c>PostgreSQL</c>, <c>MariaDB</c>).</summary>
    public string EngineName { get; }
}

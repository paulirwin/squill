using System.Text.RegularExpressions;

namespace Squill.Dacpac;

/// <summary>
/// Builds and parses the <c>DspName</c> (Database Schema Provider name) recorded on the
/// <c>DataSchemaModel</c> root of <c>model.xml</c>, mirroring how SSDT encodes the target
/// platform. SSDT writes a value like
/// <c>Microsoft.Data.Tools.Schema.Sql.Sql160DatabaseSchemaProvider</c>, where the target
/// version (<c>160</c> = SQL Server 2016) is baked into the string. Squill follows the same
/// shape, encoding both the provider and the target engine <em>major</em> version in one name:
/// <c>Squill.Data.Tools.Schema.&lt;Provider&gt;.&lt;Provider&gt;&lt;Version&gt;DatabaseSchemaProvider</c>,
/// e.g. <c>Squill.Data.Tools.Schema.Postgresql.Postgresql16DatabaseSchemaProvider</c>.
/// Only the major version is recorded — that is the platform level that gates features, so
/// point releases (e.g. MariaDB 11.4.2) are not distinguished. When no target version is set,
/// the version segment is omitted:
/// <c>Squill.Data.Tools.Schema.Postgresql.PostgresqlDatabaseSchemaProvider</c>.
/// </summary>
public static partial class DspName
{
    private const string Prefix = "Squill.Data.Tools.Schema.";
    private const string Suffix = "DatabaseSchemaProvider";

    /// <summary>
    /// Builds the DSP name for a provider and optional target major version. An empty or
    /// whitespace version yields a versionless name.
    /// </summary>
    public static string Build(string providerName, int? targetMajorVersion)
    {
        if (string.IsNullOrWhiteSpace(providerName))
        {
            throw new ArgumentException("Provider name is required.", nameof(providerName));
        }

        if (targetMajorVersion is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetMajorVersion), targetMajorVersion, "Target version cannot be negative.");
        }

        return $"{Prefix}{providerName}.{providerName}{targetMajorVersion}{Suffix}";
    }

    /// <summary>
    /// Parses a DSP name previously produced by <see cref="Build"/> back into its provider
    /// name and target major version. Returns <c>false</c> when the string is not a Squill
    /// DSP name. A versionless name yields a <c>null</c> <paramref name="targetMajorVersion"/>.
    /// </summary>
    public static bool TryParse(string? dspName, out string providerName, out int? targetMajorVersion)
    {
        providerName = string.Empty;
        targetMajorVersion = null;

        if (string.IsNullOrWhiteSpace(dspName))
        {
            return false;
        }

        var match = DspNamePattern().Match(dspName);
        if (!match.Success)
        {
            return false;
        }

        providerName = match.Groups["provider"].Value;
        var version = match.Groups["version"].Value;
        targetMajorVersion = string.IsNullOrEmpty(version)
            ? null
            : int.Parse(version, System.Globalization.CultureInfo.InvariantCulture);

        return true;
    }

    [GeneratedRegex(
        @"^Squill\.Data\.Tools\.Schema\.(?<provider>[^.]+)\.\k<provider>(?<version>\d*)DatabaseSchemaProvider$")]
    private static partial Regex DspNamePattern();
}

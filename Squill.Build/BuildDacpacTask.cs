using Microsoft.Build.Framework;
using Squill.Core;
using Squill.Dacpac;
using Squill.Provider.MariaDb;
using Squill.Provider.Postgres;

namespace Squill.Build;

/// <summary>
/// MSBuild task that builds a DACPAC from a set of declarative SQL source files.
/// It is invoked by the <c>Squill.Sdk</c> targets when a <c>.squillproj</c> is built,
/// producing <c>&lt;OutputPath&gt;&lt;DacpacFileName&gt;</c> — mirroring how SSDT emits
/// a DACPAC into <c>bin\</c>. The provider named by <see cref="ProviderName"/> selects the
/// parser used to build the model (PostgreSQL, or MariaDB/MySQL).
/// </summary>
public class BuildDacpacTask : Microsoft.Build.Utilities.Task
{
    // The providers this task can build for. The one used is chosen from ProviderName.
    private static readonly SquillProviderRegistry ProviderRegistry = new SquillProviderRegistry()
        .Register(new PostgresSquillProvider())
        .Register(new MariaDbSquillProvider());

    /// <summary>The declarative SQL files to compile into the model.</summary>
    [Required]
    public ITaskItem[] SourceFiles { get; set; } = [];

    /// <summary>Full path of the DACPAC file to write.</summary>
    [Required]
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// The database provider the model is built for and recorded in the DACPAC's Origin.xml:
    /// <c>Postgresql</c> (default), or <c>MariaDb</c> / <c>MySql</c>.
    /// </summary>
    public string ProviderName { get; set; } = "Postgresql";

    /// <summary>The data-tier application name recorded in DacMetadata.xml.</summary>
    public string DacName { get; set; } = "Squill";

    /// <summary>The data-tier application version recorded in DacMetadata.xml.</summary>
    public string DacVersion { get; set; } = "1.0.0.0";

    /// <summary>
    /// The minimum target database engine major version the DACPAC is built for (e.g. <c>16</c>
    /// for PostgreSQL, <c>11</c> for MariaDB), like SSDT's target platform. Recorded in the
    /// DACPAC and enforced at deploy time. Empty means no version constraint.
    /// </summary>
    public string TargetVersion { get; set; } = string.Empty;

    public override bool Execute()
    {
        try
        {
            var sourcePaths = SourceFiles
                .Select(i => i.GetMetadata("FullPath"))
                .Where(p => !string.IsNullOrEmpty(p))
                .ToArray();

            if (sourcePaths.Length == 0)
            {
                // Coded so it can be suppressed with NoWarn or escalated with
                // WarningsAsErrors like any other MSBuild warning (issue #61).
                Log.LogWarning(
                    subcategory: null,
                    warningCode: SqlSourceDiagnostic.NoSourceFiles,
                    helpKeyword: null,
                    file: null,
                    lineNumber: 0,
                    columnNumber: 0,
                    endLineNumber: 0,
                    endColumnNumber: 0,
                    message: "Squill: no SQL source files were provided; the DACPAC will be empty.");
            }

            // Resolve the provider named by the project (ProviderName) so its parser builds
            // the model; an unknown name fails the build with a clear diagnostic.
            var provider = ProviderRegistry.Resolve(ProviderName);

            var workspace = new Workspace();
            foreach (var path in sourcePaths)
            {
                workspace.Files.Add(new FileSystemFile(path, FileKind.Compile));
            }

            var metadata = new ModelMetadata
            {
                ProviderName = ProviderName,
                Name = DacName,
                Version = DacVersion,
                TargetMajorVersion = ParseTargetVersion(TargetVersion),
            };

            // MSBuild tasks are synchronous; block on the async build. There is no
            // synchronization context in the MSBuild host, so this cannot deadlock.
            var warnings = BuildAsync(provider, workspace, metadata, OutputPath)
                .GetAwaiter().GetResult();

            // Constructs that were declared but not modeled don't fail the build, but they
            // won't round-trip — report them so the gap is visible (issue #61).
            foreach (var warning in warnings)
            {
                LogSourceWarning(warning);
            }

            Log.LogMessage(MessageImportance.High, $"Squill: wrote DACPAC to {OutputPath}");

            return true;
        }
        catch (SqlSourceException ex)
        {
            LogSourceError(ex);
            return false;
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(i => i is SqlSourceException))
        {
            // The builders aggregate multiple source errors (e.g. several unresolved
            // foreign keys) so a build reports them all, each at its own position.
            foreach (var inner in ex.InnerExceptions.Cast<SqlSourceException>())
            {
                LogSourceError(inner);
            }

            return false;
        }
        catch (Exception ex)
        {
            // LogErrorFromException reports the message through MSBuild so the build
            // fails cleanly with a diagnostic rather than crashing the host.
            Log.LogErrorFromException(ex, showStackTrace: true);
            return false;
        }
    }

    // Reports a source-anchored error as a regular MSBuild diagnostic with file/line/column
    // metadata, so the build fails and the IDE can navigate to the offending SQL (issue #53).
    private void LogSourceError(SqlSourceException ex)
        => Log.LogError(
            subcategory: null,
            errorCode: ex.Code,
            helpKeyword: null,
            file: ex.SourceFile,
            lineNumber: ex.Line ?? 0,
            columnNumber: ex.Column ?? 0,
            endLineNumber: 0,
            endColumnNumber: 0,
            message: ex.Message);

    // Reports a build warning as a regular MSBuild diagnostic with file/line/column metadata,
    // so NoWarn / WarningsAsErrors / TreatWarningsAsErrors apply to it and the IDE can
    // navigate to the construct that will not round-trip (issue #61).
    private void LogSourceWarning(SqlSourceDiagnostic warning)
        => Log.LogWarning(
            subcategory: null,
            warningCode: warning.Code,
            helpKeyword: null,
            file: warning.SourceFile,
            lineNumber: warning.Line ?? 0,
            columnNumber: warning.Column ?? 0,
            endLineNumber: 0,
            endColumnNumber: 0,
            message: warning.Message);

    /// <summary>
    /// Parses the <see cref="TargetVersion"/> MSBuild property (a string, possibly empty or a
    /// full version like <c>16.2</c>) into a target major version. Empty yields <c>null</c>
    /// (no constraint); a dotted version keeps only the major component. A non-numeric value
    /// fails the build with a clear diagnostic.
    /// </summary>
    private static int? ParseTargetVersion(string targetVersion)
    {
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            return null;
        }

        var major = targetVersion.Trim().Split('.', 2)[0];

        if (!int.TryParse(major, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException(
                $"SquillTargetVersion '{targetVersion}' is not a valid version number "
                + "(expected a major version like '16', or a dotted version like '16.2').");
        }

        return value;
    }

    private static async Task<IReadOnlyList<SqlSourceDiagnostic>> BuildAsync(
        ISquillProvider provider, Workspace workspace, ModelMetadata metadata, string outputPath)
    {
        var result = await provider.BuildModelAsync(workspace);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using (var stream = File.Create(outputPath))
        {
            await DacpacSerializer.Serialize(metadata, result.Model, stream);
        }

        return result.Warnings;
    }
}

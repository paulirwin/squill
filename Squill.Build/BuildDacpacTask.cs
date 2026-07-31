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

    /// <summary>
    /// SQL files run against the target database <em>before</em> the schema diff is applied.
    /// Unlike <see cref="SourceFiles"/> these are imperative scripts, not declarations: they
    /// are stored verbatim in the DACPAC and never parsed into the model. Multiple files are
    /// concatenated in item order.
    /// </summary>
    public ITaskItem[] PreDeployFiles { get; set; } = [];

    /// <summary>
    /// SQL files run against the target database <em>after</em> the schema diff is applied —
    /// typically seeding or data preparation. See <see cref="PreDeployFiles"/>.
    /// </summary>
    public ITaskItem[] PostDeployFiles { get; set; } = [];

    /// <summary>Full path of the DACPAC file to write.</summary>
    [Required]
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>
    /// The database provider the model is built for and recorded in the DACPAC's Origin.xml:
    /// <c>Postgresql</c> (default), or <c>MariaDb</c> / <c>MySql</c>.
    /// </summary>
    public string ProviderName { get; set; } = "Postgresql";

    /// <summary>
    /// The name of the project being built, used only for the MSBuild
    /// "&lt;project&gt; -&gt; &lt;output&gt;" completion message. Passed in from the targets because
    /// <c>BuildEngine.ProjectFileOfTaskNode</c> reports the SDK targets file that declares
    /// the task, not the consuming <c>.squillproj</c>.
    /// </summary>
    public string ProjectName { get; set; } = "Squill";

    /// <summary>The data-tier application name recorded in DacMetadata.xml.</summary>
    public string DacName { get; set; } = "Squill";

    /// <summary>The data-tier application version recorded in DacMetadata.xml.</summary>
    public string DacVersion { get; set; } = "1.0.0.0";

    /// <summary>
    /// The oldest database engine version the DACPAC must work against, like SSDT's target
    /// platform — a bare major (<c>16</c>), a dotted version (<c>8.4</c>), or a full one
    /// (<c>8.0.13</c>). Recorded in the DACPAC and enforced at deploy time. Empty means no
    /// version constraint.
    ///
    /// <para>
    /// This is a floor with no ceiling: a newer server always satisfies it. Components left off
    /// mean <c>.0</c>, so a bare major names that major's oldest release and features added in a
    /// later point release are flagged. See <see cref="Squill.Dacpac.TargetVersion"/>.
    /// </para>
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
                // Coded so it can be suppressed with MSBuildWarningsAsMessages or escalated
                // with MSBuildWarningsAsErrors like any other MSBuild warning (issue #61).
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
                TargetVersion = Squill.Dacpac.TargetVersion.Parse(TargetVersion),
                PreDeployScript = ReadDeployScript(PreDeployFiles, "pre-deployment"),
                PostDeployScript = ReadDeployScript(PostDeployFiles, "post-deployment"),
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

            // MSBuild's conventional "<project> -> <absolute output path>" high-importance
            // message. The terminal logger parses this exact shape to render the per-project
            // "succeeded ... -> path" line (and the clickable link), so a custom-worded
            // message would build the DACPAC but report nothing. Mirrors what
            // CopyFilesToOutputDirectory logs for an ordinary assembly.
            Log.LogMessage(
                MessageImportance.High,
                $"{ProjectName} -> {Path.GetFullPath(OutputPath)}");

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
    // so MSBuildWarningsAsMessages / MSBuildWarningsAsErrors apply to it and the IDE can
    // navigate to the construct that will not round-trip (issue #61).
    //
    // Those are the MSBuild-prefixed properties: NoWarn and WarningsAsErrors are Roslyn
    // compiler options and do not affect a warning logged by a task. Measured.
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
    /// Reads the deploy-script items into the single script string stored in the DACPAC.
    /// Files are concatenated in item order, each preceded by a comment naming its source
    /// so a failure at deploy time can be traced back to the file it came from. A missing
    /// file is a build error rather than a silently skipped script.
    /// </summary>
    private string ReadDeployScript(ITaskItem[] items, string description)
    {
        var paths = items
            .Select(i => i.GetMetadata("FullPath"))
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();

        if (paths.Length == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();

        foreach (var path in paths)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"Squill: the {description} script '{path}' was not found.", path);
            }

            builder.Append("-- Squill ").Append(description).Append(" script: ")
                .AppendLine(Path.GetFileName(path));
            builder.AppendLine(File.ReadAllText(path));
            builder.AppendLine();
        }

        Log.LogMessage(
            MessageImportance.Normal,
            $"Squill: included {paths.Length} {description} script file(s).");

        return builder.ToString();
    }

    private static async Task<IReadOnlyList<SqlSourceDiagnostic>> BuildAsync(
        ISquillProvider provider, Workspace workspace, ModelMetadata metadata, string outputPath)
    {
        var result = await provider.BuildModelAsync(workspace, metadata);

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

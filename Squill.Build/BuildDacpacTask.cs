using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Squill.Dacpac;
using Squill.Provider.Postgres;

namespace Squill.Build;

/// <summary>
/// MSBuild task that builds a DACPAC from a set of declarative SQL source files.
/// It is invoked by the <c>Squill.Sdk</c> targets when a <c>.squillproj</c> is built,
/// producing <c>&lt;OutputPath&gt;&lt;DacpacFileName&gt;</c> — mirroring how SSDT emits
/// a DACPAC into <c>bin\</c>.
/// </summary>
public class BuildDacpacTask : Microsoft.Build.Utilities.Task
{
    /// <summary>The declarative SQL files to compile into the model.</summary>
    [Required]
    public ITaskItem[] SourceFiles { get; set; } = [];

    /// <summary>Full path of the DACPAC file to write.</summary>
    [Required]
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>The database provider name recorded in the DACPAC's Origin.xml.</summary>
    public string ProviderName { get; set; } = "Postgresql";

    /// <summary>The data-tier application name recorded in DacMetadata.xml.</summary>
    public string DacName { get; set; } = "Squill";

    /// <summary>The data-tier application version recorded in DacMetadata.xml.</summary>
    public string DacVersion { get; set; } = "1.0.0.0";

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
                Log.LogWarning("Squill: no SQL source files were provided; the DACPAC will be empty.");
            }

            var workspace = DacpacBuilder.CreateWorkspace(sourcePaths);

            var metadata = new ModelMetadata
            {
                ProviderName = ProviderName,
                Name = DacName,
                Version = DacVersion,
            };

            // MSBuild tasks are synchronous; block on the async build. There is no
            // synchronization context in the MSBuild host, so this cannot deadlock.
            DacpacBuilder
                .BuildToFileAsync(workspace, metadata, OutputPath)
                .GetAwaiter()
                .GetResult();

            Log.LogMessage(MessageImportance.High, $"Squill: wrote DACPAC to {OutputPath}");

            return true;
        }
        catch (Exception ex)
        {
            // LogErrorFromException reports the message through MSBuild so the build
            // fails cleanly with a diagnostic rather than crashing the host.
            Log.LogErrorFromException(ex, showStackTrace: true);
            return false;
        }
    }
}

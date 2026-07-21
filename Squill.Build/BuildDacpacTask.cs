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
            };

            // MSBuild tasks are synchronous; block on the async build. There is no
            // synchronization context in the MSBuild host, so this cannot deadlock.
            BuildAsync(provider, workspace, metadata, OutputPath).GetAwaiter().GetResult();

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

    private static async Task BuildAsync(
        ISquillProvider provider, Workspace workspace, ModelMetadata metadata, string outputPath)
    {
        var model = await provider.BuildModelAsync(workspace);

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(outputPath);
        await DacpacSerializer.Serialize(metadata, model, stream);
    }
}

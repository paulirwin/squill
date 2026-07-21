using Squill.Core;
using Squill.Dacpac;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Builds a DACPAC from a workspace of declarative MariaDB SQL source files, using the
/// ANTLR-based parser (no live database required). Mirrors the Postgres provider's build
/// path so both hosts (the MSBuild task and the console <c>build</c> verb) produce the same
/// DACPAC from the same inputs.
/// </summary>
public static class DacpacBuilder
{
    public static async Task BuildAsync(
        Workspace workspace,
        ModelMetadata metadata,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var model = await BuildModelAsync(workspace, cancellationToken);

        await DacpacSerializer.Serialize(metadata, model, stream, cancellationToken);
    }

    public static async Task BuildToFileAsync(
        Workspace workspace,
        ModelMetadata metadata,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(outputPath);

        await BuildAsync(workspace, metadata, stream, cancellationToken);
    }

    public static Task<Model> BuildModelAsync(
        Workspace workspace,
        CancellationToken cancellationToken = default)
    {
        var parser = new AntlrMariaDbParser();
        var modelBuilder = new ParserWorkspaceModelBuilder(workspace, parser);

        return modelBuilder.ExtractModelAsync(cancellationToken);
    }

    public static Workspace CreateWorkspace(IEnumerable<string> sourceFilePaths)
    {
        var workspace = new Workspace();

        foreach (var path in sourceFilePaths)
        {
            workspace.Files.Add(new FileSystemFile(path, FileKind.Compile));
        }

        return workspace;
    }
}

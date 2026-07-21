using Squill.Core;
using Squill.Dacpac;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres;

/// <summary>
/// Builds a DACPAC from a workspace of declarative SQL source files, using the
/// ANTLR-based parser (no live database required). This is the shared build path
/// used by both the MSBuild task (<c>Squill.Build</c>) and the console
/// <c>build</c> verb, so both produce byte-identical DACPACs from the same inputs.
/// </summary>
public static class DacpacBuilder
{
    /// <summary>
    /// Parses every <see cref="FileKind.Compile"/> file in <paramref name="workspace"/>
    /// into a <see cref="Model"/> and serializes it to a DACPAC written to
    /// <paramref name="stream"/>.
    /// </summary>
    public static async Task BuildAsync(
        Workspace workspace,
        ModelMetadata metadata,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var result = await BuildModelAsync(workspace, cancellationToken);

        await DacpacSerializer.Serialize(metadata, result.Model, stream, cancellationToken);
    }

    /// <summary>
    /// Parses every <see cref="FileKind.Compile"/> file in <paramref name="workspace"/>
    /// into a <see cref="Model"/> and serializes it to a DACPAC written to the file at
    /// <paramref name="outputPath"/>, creating the containing directory if needed.
    /// </summary>
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

    /// <summary>
    /// Parses every <see cref="FileKind.Compile"/> file in <paramref name="workspace"/>
    /// into a <see cref="Model"/> without serializing it, along with any build warnings.
    /// </summary>
    public static Task<BuildResult> BuildModelAsync(
        Workspace workspace,
        CancellationToken cancellationToken = default)
    {
        var parser = new AntlrPostgresParser();
        var modelBuilder = new ParserWorkspaceModelBuilder(workspace, parser);

        return modelBuilder.ExtractModelAsync(cancellationToken);
    }

    /// <summary>
    /// Builds a <see cref="Workspace"/> whose <see cref="FileKind.Compile"/> files are
    /// the given source paths on disk.
    /// </summary>
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

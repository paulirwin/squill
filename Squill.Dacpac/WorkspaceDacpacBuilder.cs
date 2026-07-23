using Squill.Core;

namespace Squill.Dacpac;

/// <summary>
/// The provider-agnostic DACPAC build path: parse a workspace of declarative SQL into a
/// <see cref="Model"/> and serialize it to a DACPAC. The only provider-specific step is
/// turning a <see cref="Workspace"/> into the right <see cref="IWorkspaceModelBuilder"/>
/// (each provider has its own ANTLR parser and model builder), supplied here as a factory.
/// Each provider's <c>DacpacBuilder</c> is a thin wrapper over these helpers, so both provider
/// build paths — and both hosts (the MSBuild task and the console <c>build</c> verb) — produce
/// byte-identical DACPACs from the same inputs.
/// </summary>
public static class WorkspaceDacpacBuilder
{
    /// <summary>
    /// Parses every <see cref="FileKind.Compile"/> file in <paramref name="workspace"/> into a
    /// <see cref="Model"/> (via the builder from <paramref name="modelBuilderFactory"/>) and
    /// serializes it to a DACPAC written to <paramref name="stream"/>.
    /// </summary>
    public static async Task BuildAsync(
        Workspace workspace,
        ModelMetadata metadata,
        Stream stream,
        Func<Workspace, IWorkspaceModelBuilder> modelBuilderFactory,
        CancellationToken cancellationToken = default)
    {
        var result = await BuildModelAsync(workspace, modelBuilderFactory, cancellationToken);

        await DacpacSerializer.Serialize(metadata, result.Model, stream, cancellationToken);
    }

    /// <summary>
    /// Parses every <see cref="FileKind.Compile"/> file in <paramref name="workspace"/> into a
    /// <see cref="Model"/> and serializes it to a DACPAC at <paramref name="outputPath"/>,
    /// creating the containing directory if needed.
    /// </summary>
    public static async Task BuildToFileAsync(
        Workspace workspace,
        ModelMetadata metadata,
        string outputPath,
        Func<Workspace, IWorkspaceModelBuilder> modelBuilderFactory,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(outputPath);

        await BuildAsync(workspace, metadata, stream, modelBuilderFactory, cancellationToken);
    }

    /// <summary>
    /// Parses every <see cref="FileKind.Compile"/> file in <paramref name="workspace"/> into a
    /// <see cref="Model"/> without serializing it, along with any build warnings.
    /// </summary>
    public static Task<BuildResult> BuildModelAsync(
        Workspace workspace,
        Func<Workspace, IWorkspaceModelBuilder> modelBuilderFactory,
        CancellationToken cancellationToken = default)
    {
        var modelBuilder = modelBuilderFactory(workspace);

        return modelBuilder.ExtractModelAsync(cancellationToken);
    }

    /// <summary>
    /// Builds a <see cref="Workspace"/> whose <see cref="FileKind.Compile"/> files are the
    /// given source paths on disk.
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

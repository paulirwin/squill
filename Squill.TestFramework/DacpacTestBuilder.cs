using Squill.Core;
using Squill.Dacpac;

namespace Squill.TestFramework;

/// <summary>
/// Builds a DACPAC from inline SQL for a test: writes the schema to a <c>.sql</c> file, wraps
/// it in a workspace, and serializes it with the given <see cref="ModelMetadata"/>. This is the
/// shared form of the many per-file <c>BuildDacpacAsync</c> copies across the integration tests;
/// the provider-specific model builder (each provider has its own ANTLR parser) is supplied as
/// a factory, and everything else — provider name, output layout, target version, pre/post-deploy
/// scripts — is a parameter.
/// </summary>
public static class DacpacTestBuilder
{
    /// <summary>
    /// Writes <paramref name="schemaSql"/> to <c>{label}.sql</c> under <paramref name="directory"/>,
    /// builds a DACPAC from it named <c>{label}.dacpac</c>, and returns the DACPAC path.
    /// </summary>
    /// <param name="directory">The working directory for the source and output files.</param>
    /// <param name="schemaSql">The declarative SQL to build.</param>
    /// <param name="providerName">The provider name recorded in the DACPAC (e.g. <c>Postgresql</c>, <c>MariaDb</c>).</param>
    /// <param name="modelBuilderFactory">Creates the provider's model builder for a workspace.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <param name="label">Base name for the <c>.sql</c> and <c>.dacpac</c> files (default <c>schema</c>/<c>TestDb</c>).</param>
    /// <param name="name">The DACPAC's <see cref="ModelMetadata.Name"/> (default <c>TestDb</c>).</param>
    /// <param name="version">The DACPAC's <see cref="ModelMetadata.Version"/> (default <c>1.0.0.0</c>).</param>
    /// <param name="targetMajorVersion">The target engine major version, or null for none.</param>
    /// <param name="preDeploy">The pre-deployment script, or empty for none.</param>
    /// <param name="postDeploy">The post-deployment script, or empty for none.</param>
    /// <param name="outputSubdirectory">A subdirectory under <paramref name="directory"/> for the DACPAC (e.g. <c>bin</c>), or null to write alongside the source.</param>
    /// <param name="fileName">The DACPAC file's base name (without extension), or null to use <paramref name="name"/>. Use this when the file name must differ from the metadata <see cref="ModelMetadata.Name"/>.</param>
    public static async Task<string> BuildToFileAsync(
        string directory,
        string schemaSql,
        string providerName,
        Func<Workspace, IWorkspaceModelBuilder> modelBuilderFactory,
        CancellationToken cancellationToken,
        string label = "schema",
        string name = "TestDb",
        string version = "1.0.0.0",
        int? targetMajorVersion = null,
        string preDeploy = "",
        string postDeploy = "",
        string? outputSubdirectory = null,
        string? fileName = null)
    {
        var sqlPath = Path.Combine(directory, $"{label}.sql");
        await File.WriteAllTextAsync(sqlPath, schemaSql, cancellationToken);

        var workspace = WorkspaceDacpacBuilder.CreateWorkspace([sqlPath]);
        var metadata = new ModelMetadata
        {
            Name = name,
            Version = version,
            ProviderName = providerName,
            TargetMajorVersion = targetMajorVersion,
            PreDeployScript = preDeploy,
            PostDeployScript = postDeploy,
        };

        var outputDirectory = outputSubdirectory is null
            ? directory
            : Path.Combine(directory, outputSubdirectory);
        var dacpacPath = Path.Combine(outputDirectory, $"{fileName ?? name}.dacpac");

        await WorkspaceDacpacBuilder.BuildToFileAsync(
            workspace, metadata, dacpacPath, modelBuilderFactory, cancellationToken);

        return dacpacPath;
    }
}

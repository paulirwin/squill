using Squill.Core;

namespace Squill.Dacpac;

/// <summary>
/// Deploys or scripts a DACPAC by first reading the provider name recorded in the DACPAC and
/// resolving the matching <see cref="ISquillProvider"/> from a
/// <see cref="SquillProviderRegistry"/>. This is how a single host (the CLI) targets either
/// PostgreSQL or MariaDB/MySQL from the same command, without the caller knowing which
/// provider a given DACPAC was built for.
/// </summary>
public static class DacpacProviderDispatch
{
    public static async Task<DeployResult> DeployFromFileAsync(
        SquillProviderRegistry registry,
        string dacpacPath,
        string connectionString,
        string? targetDatabaseName = null,
        bool dryRun = false,
        IProgress<string>? progress = null,
        DeployOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // The DACPAC stream is read twice — once to peek the provider name, then by the
        // resolved provider to deploy — so open it from the path for each read rather than
        // sharing one non-seekable stream.
        var provider = await ResolveProviderAsync(registry, dacpacPath, cancellationToken);

        await using var stream = File.OpenRead(dacpacPath);

        return await provider.DeployAsync(
            stream, connectionString, targetDatabaseName, dryRun, progress, options,
            cancellationToken);
    }

    public static async Task<DeployResult> ScriptFromFileAsync(
        SquillProviderRegistry registry,
        string dacpacPath,
        string connectionString,
        string? targetDatabaseName = null,
        IProgress<string>? progress = null,
        DeployOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var provider = await ResolveProviderAsync(registry, dacpacPath, cancellationToken);

        await using var stream = File.OpenRead(dacpacPath);

        return await provider.ScriptAsync(
            stream, connectionString, targetDatabaseName, progress, options, cancellationToken);
    }

    // Reads just the DACPAC's metadata to learn its provider name, then resolves the
    // matching provider from the registry.
    private static async Task<ISquillProvider> ResolveProviderAsync(
        SquillProviderRegistry registry,
        string dacpacPath,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(dacpacPath);

        var (metadata, _) = await DacpacSerializer.Deserialize(stream, cancellationToken);

        return registry.Resolve(metadata.ProviderName);
    }
}

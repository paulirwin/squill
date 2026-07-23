using System.Reflection;
using Squill.Core;

namespace Squill.TestFramework;

/// <summary>
/// An <see cref="IFile"/> backed by an embedded resource in a test assembly, so tests can load
/// <c>.sql</c> fixtures by their manifest resource name (e.g.
/// <c>Squill.IntegrationTests.Postgres.DacpacDeployTest.Schema.sql</c>).
/// </summary>
/// <remarks>
/// The resource lives in the <em>test</em> assembly, not this framework assembly, so the owning
/// assembly must be supplied — either directly or via a marker type from that assembly.
/// </remarks>
public class EmbeddedResourceFile : IFile
{
    private readonly Assembly _assembly;

    public EmbeddedResourceFile(string name, FileKind kind, Assembly assembly)
    {
        Name = name;
        Kind = kind;
        _assembly = assembly;
    }

    /// <summary>
    /// Resolves the resource from the assembly that declares <paramref name="assemblyMarker"/>.
    /// </summary>
    public EmbeddedResourceFile(string name, FileKind kind, Type assemblyMarker)
        : this(name, kind, assemblyMarker.Assembly)
    {
    }

    public string Name { get; }

    public FileKind Kind { get; }

    public async Task<string> ReadAllTextAsync(CancellationToken cancellationToken = default)
    {
        await using var stream = _assembly.GetManifestResourceStream(Name);

        if (stream == null)
        {
            throw new InvalidOperationException($"Could not find embedded resource {Name}");
        }

        using var sr = new StreamReader(stream);

        return await sr.ReadToEndAsync(cancellationToken);
    }
}

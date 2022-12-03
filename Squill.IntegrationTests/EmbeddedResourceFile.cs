using Squill.Core;

namespace Squill.IntegrationTests;

public class EmbeddedResourceFile : IFile
{
    public EmbeddedResourceFile(string name, FileKind kind)
    {
        Name = name;
        Kind = kind;
    }
    
    public string Name { get; }
    
    public FileKind Kind { get; }
    
    public async Task<string> ReadAllTextAsync(CancellationToken cancellationToken = default)
    {
        var assembly = GetType().Assembly;
        await using var stream = assembly.GetManifestResourceStream(Name);

        if (stream == null)
        {
            throw new InvalidOperationException($"Could not find embedded resource {Name}");
        }
        
        using var sr = new StreamReader(stream);

        return await sr.ReadToEndAsync(cancellationToken);
    }
}
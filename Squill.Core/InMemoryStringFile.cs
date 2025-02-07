namespace Squill.Core;

public class InMemoryStringFile : IFile
{
    public InMemoryStringFile(string name, FileKind kind, string contents)
    {
        Name = name;
        Kind = kind;
        Contents = contents;
    }

    public string Name { get; }
    
    public FileKind Kind { get; }

    public string Contents { get; }
    
    public Task<string> ReadAllTextAsync(CancellationToken cancellationToken = default) 
        => Task.FromResult(Contents);
}
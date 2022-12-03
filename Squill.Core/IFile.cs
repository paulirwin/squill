namespace Squill.Core;

public interface IFile
{
    string Name { get; }
    
    FileKind Kind { get; }

    Task<string> ReadAllTextAsync(CancellationToken cancellationToken = default);
}
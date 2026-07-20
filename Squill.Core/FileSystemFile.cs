namespace Squill.Core;

/// <summary>
/// An <see cref="IFile"/> backed by a file on disk. The <see cref="Name"/> is the
/// path used to read the file's contents on demand.
/// </summary>
public class FileSystemFile : IFile
{
    public FileSystemFile(string path, FileKind kind)
    {
        Name = path;
        Kind = kind;
    }

    public string Name { get; }

    public FileKind Kind { get; }

    public Task<string> ReadAllTextAsync(CancellationToken cancellationToken = default)
        => File.ReadAllTextAsync(Name, cancellationToken);
}

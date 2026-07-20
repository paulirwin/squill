namespace Squill.Core;

/// <summary>
/// An update of an installed extension to the version pinned by the source, scripted as
/// <c>ALTER EXTENSION ... UPDATE TO</c>. Produced when a source pins a version (via
/// <c>WITH VERSION</c>) that differs from the version currently installed in the target
/// database. A source that pins no version leaves the installed version unmanaged and
/// produces no delta.
/// </summary>
public class AlterExtensionVersionDelta : SchemaDelta
{
    public AlterExtensionVersionDelta(Element sourceElement, string targetVersion)
    {
        SourceElement = sourceElement;
        TargetVersion = targetVersion;
    }

    /// <summary>The extension element from the source (desired) model.</summary>
    public Element SourceElement { get; }

    /// <summary>The version to update the extension to (the source-pinned version).</summary>
    public string TargetVersion { get; }
}

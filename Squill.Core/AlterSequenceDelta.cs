namespace Squill.Core;

/// <summary>
/// A change to an existing sequence's options, scripted as <c>ALTER SEQUENCE</c> (issue #122).
///
/// A sequence is altered in place rather than dropped and recreated for two reasons: every
/// option a declaration can set is alterable, and dropping the sequence would reset its
/// current value — losing the counter, and failing outright if a column default still draws
/// from it via <c>nextval()</c>.
///
/// Both elements are carried because scripting the change needs the source (desired) options
/// and, for options the source no longer states, the knowledge that the target still has one
/// set — those must be actively reset to their default rather than left alone.
/// </summary>
public class AlterSequenceDelta : SchemaDelta
{
    public AlterSequenceDelta(Element sourceElement, Element targetElement)
    {
        SourceElement = sourceElement;
        TargetElement = targetElement;
    }

    /// <summary>The sequence element from the source (desired) model.</summary>
    public Element SourceElement { get; }

    /// <summary>The sequence element as it currently exists in the target database.</summary>
    public Element TargetElement { get; }
}

namespace Squill.Core;

/// <summary>
/// A change to an existing range type's definition, which PostgreSQL cannot script
/// (issue #122).
///
/// There is no <c>ALTER TYPE</c> form that changes a range type's subtype, operator class or
/// collation, and the type cannot be dropped and recreated while a column uses it. This delta
/// exists so the diff records the change and the script generator can report it precisely —
/// naming the old and new subtype — rather than the comparison throwing a bare
/// <c>NotImplementedException</c> that names only the element type.
/// </summary>
public class AlterRangeTypeDelta : SchemaDelta
{
    public AlterRangeTypeDelta(Element sourceElement, Element targetElement)
    {
        SourceElement = sourceElement;
        TargetElement = targetElement;
    }

    /// <summary>The range type element from the source (desired) model.</summary>
    public Element SourceElement { get; }

    /// <summary>The range type element as it currently exists in the target database.</summary>
    public Element TargetElement { get; }
}

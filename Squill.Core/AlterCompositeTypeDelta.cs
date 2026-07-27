namespace Squill.Core;

/// <summary>
/// A change to an existing composite type's attributes, scripted as
/// <c>ALTER TYPE ... ADD/DROP ATTRIBUTE</c> (issue #122).
///
/// A composite type is altered in place rather than dropped and recreated because
/// <c>DROP TYPE</c> fails whenever a table column is typed as the composite, so a rebuild
/// would break any schema that actually uses it.
///
/// Only added and dropped attributes can be scripted. PostgreSQL cannot change an existing
/// attribute's <em>type</em> while any table column uses the composite type — not even with
/// <c>CASCADE</c> — so <see cref="RetypedAttributes"/> records those separately and the script
/// generator reports them rather than emitting SQL that would fail at deploy.
/// </summary>
public class AlterCompositeTypeDelta : SchemaDelta
{
    public AlterCompositeTypeDelta(Element sourceElement, Element targetElement,
        IReadOnlyList<Element> addedAttributes,
        IReadOnlyList<string> droppedAttributes,
        IReadOnlyList<string> retypedAttributes)
    {
        SourceElement = sourceElement;
        TargetElement = targetElement;
        AddedAttributes = addedAttributes;
        DroppedAttributes = droppedAttributes;
        RetypedAttributes = retypedAttributes;
    }

    /// <summary>The composite type element from the source (desired) model.</summary>
    public Element SourceElement { get; }

    /// <summary>The composite type element as it currently exists in the target database.</summary>
    public Element TargetElement { get; }

    /// <summary>
    /// The attribute elements declared in the source but not present in the target, in
    /// declaration order.
    /// </summary>
    public IReadOnlyList<Element> AddedAttributes { get; }

    /// <summary>The bare names of attributes present in the target but no longer declared.</summary>
    public IReadOnlyList<string> DroppedAttributes { get; }

    /// <summary>
    /// The bare names of attributes present in both but declared with a different type. These
    /// cannot be scripted; the script generator reports them, rendering each type itself from
    /// <see cref="SourceElement"/> and <see cref="TargetElement"/> — type rendering is the
    /// generator's job, so only the names are carried here.
    /// </summary>
    public IReadOnlyList<string> RetypedAttributes { get; }
}

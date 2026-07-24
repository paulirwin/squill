namespace Squill.Core;

/// <summary>
/// The addition of one or more labels to an existing enum type, scripted as a sequence of
/// <c>ALTER TYPE ... ADD VALUE</c> (issue #122). An enum is altered in place rather than
/// dropped and recreated: <c>DROP TYPE</c> fails whenever a column is typed as the enum, so
/// a rebuild would break any schema that actually uses it.
///
/// Only additive changes reach here. A removed or reordered label has no <c>ALTER TYPE</c>
/// form in PostgreSQL and cannot be applied without rewriting the columns that use the type,
/// so the diff records the change and the script generator fails loudly on it.
/// </summary>
public class AlterEnumTypeDelta : SchemaDelta
{
    public AlterEnumTypeDelta(
        Element sourceElement,
        Element targetElement,
        IReadOnlyList<AddedEnumLabel> addedLabels)
    {
        SourceElement = sourceElement;
        TargetElement = targetElement;
        AddedLabels = addedLabels;
    }

    /// <summary>The enum element from the source (desired) model.</summary>
    public Element SourceElement { get; }

    /// <summary>The enum element as it currently exists in the target database.</summary>
    public Element TargetElement { get; }

    /// <summary>
    /// The labels to add, in source declaration order. Empty when the only difference is one
    /// that cannot be scripted, in which case the script generator reports it.
    /// </summary>
    public IReadOnlyList<AddedEnumLabel> AddedLabels { get; }
}

/// <summary>
/// A single label added to an existing enum type, and where it goes. PostgreSQL appends a new
/// label by default; a label inserted between two existing ones must say which label it comes
/// before, since an enum's declared order is significant (it is the sort order).
/// </summary>
/// <param name="Label">The label to add.</param>
/// <param name="BeforeLabel">
/// The existing label this one must precede, or <c>null</c> to append at the end.
/// </param>
public readonly record struct AddedEnumLabel(string Label, string? BeforeLabel);

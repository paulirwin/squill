namespace Squill.Core;

/// <summary>
/// The drop-and-recreate of an object that exists in both models but whose definition
/// changed and cannot be altered in place. Postgres has no meaningful <c>ALTER</c> for a
/// definition change to an index, so the object is dropped and recreated with its desired
/// shape. Only produced for objects that hold no data (e.g. indexes), so it is never a
/// data-loss operation.
/// </summary>
public class RecreateDelta : SchemaDelta
{
    public RecreateDelta(Element sourceElement, Element targetElement)
    {
        SourceElement = sourceElement;
        TargetElement = targetElement;
    }

    /// <summary>The desired element (from the source/DACPAC model) to create.</summary>
    public Element SourceElement { get; }

    /// <summary>The current element (from the target database) to drop first.</summary>
    public Element TargetElement { get; }
}

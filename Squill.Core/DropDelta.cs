namespace Squill.Core;

/// <summary>
/// The removal of a top-level object that exists in the target database but not in the
/// source (DACPAC) model — a table, index, or extension. Only produced when
/// <see cref="DeployOptions.DropObjectsNotInSource"/> is enabled; dropping objects is
/// destructive and off by default (as in SSDT).
/// </summary>
public class DropDelta : SchemaDelta
{
    public DropDelta(Element element, bool causesDataLoss)
    {
        Element = element;
        CausesDataLoss = causesDataLoss;
    }

    /// <summary>The target-database element to drop.</summary>
    public Element Element { get; }

    /// <summary>
    /// Whether removing this object destroys data — true for a table (its rows are lost),
    /// false for an index or extension. Used to decide whether the drop is blocked by
    /// <see cref="DeployOptions.BlockOnPossibleDataLoss"/>. Determined by the provider,
    /// since which element types hold data is database-specific.
    /// </summary>
    public bool CausesDataLoss { get; }
}

namespace Squill.Core;

public class SchemaComparison
{
    public IList<SchemaDelta> Deltas { get; } = new List<SchemaDelta>();

    /// <summary>
    /// Human-readable reasons why this comparison would (or might) cause data loss — one
    /// per data-losing operation (dropping a table or column). Empty when the comparison
    /// destroys no data. Populated by <see cref="SchemaCompare"/> regardless of the
    /// block-on-data-loss option, so a caller can surface the warnings (e.g. on a dry run)
    /// and separately decide whether to block via
    /// <see cref="ThrowIfDataLoss"/>.
    /// </summary>
    public IList<string> DataLossReasons { get; } = new List<string>();

    /// <summary>Whether applying this comparison would (or might) cause data loss.</summary>
    public bool CausesDataLoss => DataLossReasons.Count > 0;

    /// <summary>
    /// Throws <see cref="PossibleDataLossException"/> if this comparison would cause data
    /// loss. Callers invoke this to enforce SSDT's block-on-possible-data-loss before
    /// executing — but not on a dry run, so a destructive script can still be previewed.
    /// </summary>
    public void ThrowIfDataLoss()
    {
        if (CausesDataLoss)
        {
            throw new PossibleDataLossException(DataLossReasons.ToList());
        }
    }
}

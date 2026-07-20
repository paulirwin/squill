namespace Squill.Core;

/// <summary>
/// Thrown before any SQL is executed when a deployment would (or might) cause data loss —
/// dropping a table or column, or rebuilding a table — and
/// <see cref="DeployOptions.BlockOnPossibleDataLoss"/> is enabled (the default). Mirrors
/// SSDT's block-on-possible-data-loss behavior so data is never destroyed unintentionally.
/// </summary>
public class PossibleDataLossException : Exception
{
    public PossibleDataLossException(IReadOnlyList<string> reasons)
        : base(BuildMessage(reasons))
    {
        Reasons = reasons;
    }

    /// <summary>One human-readable reason per data-loss operation that triggered the block.</summary>
    public IReadOnlyList<string> Reasons { get; }

    private static string BuildMessage(IReadOnlyList<string> reasons)
    {
        var detail = reasons.Count == 0
            ? "a change would cause data loss"
            : string.Join("; ", reasons);

        return $"Deployment blocked to prevent possible data loss: {detail}. Re-run with "
               + "block-on-possible-data-loss disabled to allow these changes.";
    }
}

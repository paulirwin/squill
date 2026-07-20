namespace Squill.Core;

/// <summary>
/// Options that control how a deployment reconciles the source (DACPAC) model with the
/// target database — mirroring a subset of SSDT's <c>DacDeployOptions</c>, using the same
/// names and defaults so behavior is familiar to SSDT/sqlpackage users.
/// </summary>
public record DeployOptions
{
    /// <summary>
    /// Whether a table change that can't be applied with an in-place ALTER may be
    /// deployed by rebuilding the table (create-copy-drop-rename). Allowed by default;
    /// when <c>false</c>, such a change throws <see cref="TableRebuildNotAllowedException"/>.
    /// (SSDT: "Allow table recreation".)
    /// </summary>
    public bool AllowTableRebuild { get; init; } = true;

    /// <summary>
    /// Whether standalone objects present in the target database but absent from the
    /// source are dropped (tables, indexes, extensions, and their dependent constraints).
    /// <strong>Off by default</strong>: dropping objects is destructive and must be opted
    /// into, exactly as in SSDT. Note this does <em>not</em> gate dropping a column from a
    /// table that still exists — that is part of the table's ALTER.
    /// (SSDT: <c>DropObjectsNotInSource</c>.)
    /// </summary>
    public bool DropObjectsNotInSource { get; init; }

    /// <summary>
    /// Whether the deploy is blocked when a change would (or might) cause data loss —
    /// dropping a table or column, or rebuilding a table. <strong>On by default</strong>,
    /// as in SSDT, so data is never destroyed unintentionally; when a blocking change is
    /// found, <see cref="PossibleDataLossException"/> is thrown before any SQL runs.
    /// (SSDT: <c>BlockOnPossibleDataLoss</c>.)
    /// </summary>
    public bool BlockOnPossibleDataLoss { get; init; } = true;

    /// <summary>The default options: rebuild allowed, no object drops, data loss blocked.</summary>
    public static DeployOptions Default { get; } = new();
}

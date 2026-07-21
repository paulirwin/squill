namespace Squill.Core;

/// <summary>
/// A non-fatal diagnostic anchored to a SQL source file: the warning counterpart to
/// <see cref="SqlSourceException"/>. The providers cannot throw for these — a dropped
/// CHECK constraint or an unmodeled function default should not fail the build — so they
/// are collected during the build and returned alongside the model, letting the host report
/// them through its own warning channel (<c>Log.LogWarning</c> for the MSBuild task) with
/// file/line/column so MSBuild's <c>NoWarn</c> / <c>WarningsAsErrors</c> apply (issue #61).
/// </summary>
/// <param name="Message">The human-readable warning text.</param>
/// <param name="SourceFile">The source file the warning is about, as named by the workspace.</param>
/// <param name="Line">The 1-based line of the construct, or null when unknown.</param>
/// <param name="Column">The 1-based column of the construct, or null when unknown.</param>
/// <param name="Code">The <c>SQ1xxx</c> diagnostic code the host should report.</param>
public readonly record struct SqlSourceDiagnostic(
    string Message,
    string SourceFile,
    int? Line = null,
    int? Column = null,
    string Code = SqlSourceDiagnostic.UnmodeledConstruct)
{
    /// <summary>
    /// Diagnostic code for a project that contributed no SQL source files, so the DACPAC is
    /// empty. Coded (rather than an uncoded warning) so it can be suppressed or escalated
    /// like any other MSBuild warning.
    /// </summary>
    public const string NoSourceFiles = "SQ1001";

    /// <summary>
    /// Diagnostic code for a construct that was recognized in the source but is not carried
    /// into the model — an unsupported statement (<c>CREATE VIEW</c>), an ignored constraint
    /// (CHECK, COMMENT, COLLATE), or a default expression that is not a constant literal.
    /// Anything declared-but-not-modeled will not round-trip, so it is worth a warning.
    /// </summary>
    public const string UnmodeledConstruct = "SQ1002";
}

namespace Squill.Core;

/// <summary>
/// A non-fatal diagnostic anchored to a SQL source file: the warning counterpart to
/// <see cref="SqlSourceException"/>. The providers cannot throw for these — a dropped
/// CHECK constraint or an unmodeled function default should not fail the build — so they
/// are collected during the build and returned alongside the model, letting the host report
/// them through its own warning channel (<c>Log.LogWarning</c> for the MSBuild task) with
/// file/line/column so MSBuild's <c>MSBuildWarningsAsMessages</c> / <c>MSBuildWarningsAsErrors</c>
/// apply (issue #61).
///
/// <para>
/// Those are the <c>MSBuild</c>-prefixed properties, not the <c>NoWarn</c> / <c>WarningsAsErrors</c>
/// a C# project uses: the latter are Roslyn compiler options and have no effect on a warning
/// logged by a task. Measured — <c>NoWarn</c> leaves an <c>SQ1xxx</c> warning in the output,
/// while <c>MSBuildWarningsAsMessages</c> demotes it.
/// </para>
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

    /// <summary>
    /// Diagnostic code for source that uses a construct introduced in a <em>newer</em> engine
    /// version than the project's declared target (issue #142). Until now the target version
    /// was only enforced at deploy time, against the server's reported version — so source
    /// using a too-new construct built cleanly and then failed as a syntax error partway
    /// through the deploy, after earlier statements had already been applied.
    ///
    /// <para>
    /// A warning rather than an error, because the target version states the <em>oldest</em>
    /// server that must be supported and a project may legitimately be mid-upgrade. A project
    /// that wants it fatal escalates it with <c>&lt;MSBuildWarningsAsErrors&gt;SQ1003&lt;/…&gt;</c>
    /// — which is why this rides the diagnostic channel rather than throwing. Note the property
    /// is <c>MSBuildWarningsAsErrors</c>, not the <c>WarningsAsErrors</c> a C# project would
    /// use: the latter is a Roslyn compiler option and does not apply to a task-logged warning.
    /// </para>
    ///
    /// <para>
    /// The construct is still modeled. Dropping it would deploy an object whose semantics
    /// differ from the source's — the failure mode #141 called out for typed literals — so the
    /// warning is the whole of the response.
    /// </para>
    /// </summary>
    public const string FeatureNotInTargetVersion = "SQ1003";

    /// <summary>
    /// Diagnostic code for source that uses a construct the target <em>engine</em> does not
    /// support at any version (issue #142) — as distinct from <see cref="FeatureNotInTargetVersion"/>,
    /// where the construct exists but arrived later than the declared target.
    ///
    /// <para>
    /// The two are kept apart because the remedy differs and only one of them is a version
    /// problem. A too-new construct is fixed by raising the target version; there is no version
    /// of MySQL that adds MariaDB's <c>UUID</c> type, so telling an author it is "too new" would
    /// send them looking for an upgrade that does not exist. This matters here because one
    /// provider serves both engines: the same source is version-gated on one and impossible on
    /// the other.
    /// </para>
    /// </summary>
    public const string FeatureNotSupportedByEngine = "SQ1004";

    /// <summary>
    /// Diagnostic code for a construct every supported version of the target engine still
    /// accepts, but whose documentation says it is scheduled for removal (issue #190).
    ///
    /// <para>
    /// This is deliberately not <see cref="FeatureNotInTargetVersion"/>. That code says the
    /// declared target is too <em>old</em> for the source, and its remedy is to raise the target
    /// version. Here nothing about the target version is wrong — the construct works on every
    /// version in the supported window, including the newest — so "requires version N or later"
    /// would be a nonsense message pointing at an upgrade that fixes nothing. The remedy is to
    /// change the source, and it is version-independent: the same warning is correct whether the
    /// project targets the oldest supported major or the newest.
    /// </para>
    ///
    /// <para>
    /// The bar for reporting under this code is an explicit statement of removal or
    /// non-recommendation in the vendor's own documentation, cited in the warning. Advice to
    /// prefer something else is not deprecation, and warning on constructs that merely feel dated
    /// would train authors to suppress the code — issue #190 lists five candidates
    /// (<c>money</c>, <c>CREATE RULE</c>, <c>serial</c>, <c>INHERITS</c>, <c>character(n)</c>)
    /// checked against their documentation and deliberately <em>not</em> reported for this reason.
    /// </para>
    ///
    /// <para>
    /// The construct is still modeled, as with every other <c>SQ1xxx</c>: it deploys correctly
    /// today, and the warning is about its future. A project that wants it fatal escalates it
    /// with <c>&lt;MSBuildWarningsAsErrors&gt;SQ1006&lt;/…&gt;</c>.
    /// </para>
    /// </summary>
    public const string DeprecatedConstruct = "SQ1006";
}

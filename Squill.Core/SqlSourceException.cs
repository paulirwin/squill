namespace Squill.Core;

/// <summary>
/// An error anchored to a SQL source file: carries the file (and, when known, the 1-based
/// line and column) of the offending text so hosts — the MSBuild task in particular — can
/// report a diagnostic that points the IDE at the source. <see cref="Code"/> is the
/// diagnostic code the host should report: <c>SQ0001</c> (the default) for syntax and
/// other per-statement errors, <c>SQ0002</c> for a reference to an object that is not
/// defined in the project, <c>SQ0003</c> for a duplicate definition, <c>SQ0004</c> for a
/// constraint whose shape is invalid, <c>SQ0005</c> for an identifier the target engine
/// would reject as too long, <c>SQ0006</c> for an imperative statement (ALTER/DROP/DML)
/// authored in a declarative source file.
/// </summary>
public class SqlSourceException : Exception
{
    /// <summary>Diagnostic code for a syntax error or unsupported construct.</summary>
    public const string SyntaxError = "SQ0001";

    /// <summary>Diagnostic code for a reference to an object not defined in the project.</summary>
    public const string UnresolvedReference = "SQ0002";

    /// <summary>
    /// Diagnostic code for an object defined more than once in the project: two
    /// <c>CREATE TABLE</c>s for the same name, a duplicated column within a table, or a
    /// reused constraint/index name. Reported at the second definition.
    /// </summary>
    public const string DuplicateDefinition = "SQ0003";

    /// <summary>Diagnostic code for a constraint whose shape is invalid (e.g. a foreign key column-count mismatch).</summary>
    public const string InvalidConstraint = "SQ0004";

    /// <summary>
    /// Diagnostic code for an identifier longer than the target engine's limit. The engine
    /// would reject the DDL mid-deploy (MariaDB/MySQL <c>ERROR 1059</c>), leaving the target
    /// half-deployed, so it is caught at build time instead. Reported for a derived name
    /// (an unnamed foreign key's <c>&lt;table&gt;_ibfk_&lt;n&gt;</c>) as well as a written one,
    /// since a derived name can exceed the limit while every identifier in the source is
    /// within it.
    /// </summary>
    public const string IdentifierTooLong = "SQ0005";

    /// <summary>
    /// Diagnostic code for an imperative statement authored in a declarative source file
    /// (issue #125): an <c>ALTER</c>, <c>DROP</c> or <c>TRUNCATE</c>, or DML such as
    /// <c>INSERT</c>. Source files state the desired end-state as <c>CREATE</c> and the diff
    /// engine generates the migration, so an authored one has no meaning here — it is a
    /// mistake in the source, not a gap in Squill.
    ///
    /// <para>
    /// It has its own code because the two things it replaced both misdescribed it. Postgres
    /// reported <c>SQ0001</c> carrying the internal "Expected VisitStmt to return a Statement",
    /// and MariaDB an <c>SQ1002</c> "not modeled by Squill" — wording that reads as a missing
    /// capability and invites the author to wait for it to arrive, when the fix is to rewrite
    /// the statement. MariaDB's was also only a <em>warning</em>, so the build succeeded while
    /// the statement was silently discarded.
    /// </para>
    ///
    /// <para>
    /// An error rather than a warning precisely because the statement cannot be honoured: a
    /// warning would leave the author believing their ALTER had been applied. This is not a
    /// judgement that the SQL is wrong — it is valid SQL in the wrong place. Pre/post-deploy
    /// scripts are where imperative SQL belongs, and they are stored verbatim and never parsed,
    /// so they are unaffected.
    /// </para>
    /// </summary>
    public const string ImperativeStatement = "SQ0006";

    public SqlSourceException(
        string message,
        string sourceFile,
        int? line = null,
        int? column = null,
        string code = SyntaxError,
        Exception? innerException = null)
        : base(message, innerException)
    {
        SourceFile = sourceFile;
        Line = line;
        Column = column;
        Code = code;
    }

    /// <summary>The source file the error is in (as named by the workspace, typically a full path).</summary>
    public string SourceFile { get; }

    /// <summary>The 1-based line of the error, or null when unknown.</summary>
    public int? Line { get; }

    /// <summary>The 1-based column of the error, or null when unknown.</summary>
    public int? Column { get; }

    /// <summary>The diagnostic code to report (e.g. <c>SQ0001</c>).</summary>
    public string Code { get; }
}

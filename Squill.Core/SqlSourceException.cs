namespace Squill.Core;

/// <summary>
/// An error anchored to a SQL source file: carries the file (and, when known, the 1-based
/// line and column) of the offending text so hosts — the MSBuild task in particular — can
/// report a diagnostic that points the IDE at the source. <see cref="Code"/> is the
/// diagnostic code the host should report: <c>SQ0001</c> (the default) for syntax and
/// other per-statement errors, <c>SQ0002</c> for a reference to an object that is not
/// defined in the project.
/// </summary>
public class SqlSourceException : Exception
{
    /// <summary>Diagnostic code for a syntax error or unsupported construct.</summary>
    public const string SyntaxError = "SQ0001";

    /// <summary>Diagnostic code for a reference to an object not defined in the project.</summary>
    public const string UnresolvedReference = "SQ0002";

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

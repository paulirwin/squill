namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>CREATE [OR REPLACE] PROCEDURE name(args) LANGUAGE lang AS 'body'</c> statement.
///
/// The <see cref="Body"/> is held verbatim — exactly the characters between the quote or
/// dollar-quote delimiters — because that is precisely what PostgreSQL stores in
/// <c>pg_proc.prosrc</c>. Keeping it byte-for-byte lets a model parsed from source
/// hash-match one extracted from a live database without canonicalizing the body, which
/// would be impossible to do reliably across procedural languages.
/// </summary>
public class CreateProcedureStatement : Statement
{
    public CreateProcedureStatement(QualifiedName name, bool orReplace)
    {
        Name = name;
        OrReplace = orReplace;
    }

    public QualifiedName Name { get; }

    /// <summary>
    /// Whether OR REPLACE was written. This affects how the procedure is created, not the
    /// desired schema state, so it does not participate in the model.
    /// </summary>
    public bool OrReplace { get; }

    public IList<RoutineParameter> Parameters { get; } = new List<RoutineParameter>();

    /// <summary>The procedural language name (e.g. plpgsql, sql, plpython3u).</summary>
    public string? Language { get; set; }

    /// <summary>The routine body, verbatim as written between its quote delimiters.</summary>
    public string? Body { get; set; }

    /// <summary>
    /// Whether SECURITY DEFINER was written. INVOKER is PostgreSQL's default, so false
    /// means the procedure runs with the privileges of its caller.
    /// </summary>
    public bool SecurityDefiner { get; set; }
}

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

    /// <summary>
    /// The <c>SET</c>/<c>RESET</c> configuration clauses, in declaration order (issue #213).
    /// A procedure carries these for the same reason a function does: attaching
    /// <c>SET search_path</c> to a <c>SECURITY DEFINER</c> routine is the documented
    /// hardening idiom.
    /// </summary>
    public IList<RoutineSetting> Settings { get; } = new List<RoutineSetting>();

    /// <summary>
    /// The type names of a <c>TRANSFORM FOR TYPE ...</c> clause, in declaration order.
    /// </summary>
    public IList<string> TransformTypes { get; } = new List<string>();

    /// <summary>
    /// The link symbol of the two-string <c>AS 'obj_file', 'link_symbol'</c> form. When set,
    /// <see cref="Body"/> holds the object file rather than a routine body.
    /// </summary>
    public string? LinkSymbol { get; set; }
}

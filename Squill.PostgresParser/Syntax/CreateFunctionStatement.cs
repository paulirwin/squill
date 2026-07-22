namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>CREATE [OR REPLACE] FUNCTION name(args) RETURNS type LANGUAGE lang AS 'body'</c>
/// statement (issue #81). A function shares the <c>createfunctionstmt</c> grammar rule with a
/// procedure and carries the same facets, plus a return type and the volatility/strictness
/// attributes that describe how the planner may treat it (a procedure ignores those).
///
/// The <see cref="Body"/> is held verbatim — exactly the characters between the quote or
/// dollar-quote delimiters — because that is what PostgreSQL stores in <c>pg_proc.prosrc</c>,
/// so a model parsed from source hash-matches one extracted from a live database without
/// canonicalizing the body.
/// </summary>
public class CreateFunctionStatement : Statement
{
    public CreateFunctionStatement(QualifiedName name, bool orReplace)
    {
        Name = name;
        OrReplace = orReplace;
    }

    public QualifiedName Name { get; }

    /// <summary>
    /// Whether OR REPLACE was written. This affects how the function is created, not the
    /// desired schema state, so it does not participate in the model.
    /// </summary>
    public bool OrReplace { get; }

    public IList<RoutineParameter> Parameters { get; } = new List<RoutineParameter>();

    /// <summary>
    /// The function's return type (the <c>RETURNS</c> clause), or null when it returns
    /// nothing declared (unusual). <c>RETURNS SETOF x</c> sets <see cref="ReturnsSet"/>.
    /// </summary>
    public DataType? ReturnType { get; set; }

    /// <summary>Whether the return was written <c>RETURNS SETOF &lt;type&gt;</c>.</summary>
    public bool ReturnsSet { get; set; }

    /// <summary>The procedural language name (e.g. plpgsql, sql).</summary>
    public string? Language { get; set; }

    /// <summary>The routine body, verbatim as written between its quote delimiters.</summary>
    public string? Body { get; set; }

    /// <summary>
    /// Whether SECURITY DEFINER was written. INVOKER is PostgreSQL's default (false).
    /// </summary>
    public bool SecurityDefiner { get; set; }

    /// <summary>
    /// The function's volatility, or null when unwritten (PostgreSQL defaults to VOLATILE).
    /// One of <c>IMMUTABLE</c>, <c>STABLE</c>, <c>VOLATILE</c>.
    /// </summary>
    public FunctionVolatility? Volatility { get; set; }

    /// <summary>
    /// Whether the function was declared <c>STRICT</c> (a.k.a. <c>RETURNS NULL ON NULL
    /// INPUT</c>). Null when unwritten (PostgreSQL defaults to <c>CALLED ON NULL INPUT</c>).
    /// </summary>
    public bool? Strict { get; set; }
}

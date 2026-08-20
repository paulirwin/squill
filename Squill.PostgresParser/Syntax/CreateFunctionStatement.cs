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

    /// <summary>
    /// The <c>SET</c>/<c>RESET</c> configuration clauses, in declaration order (issue #213).
    /// Order is preserved because PostgreSQL applies them in the order written and stores
    /// <c>proconfig</c> in that same order.
    /// </summary>
    public IList<RoutineSetting> Settings { get; } = new List<RoutineSetting>();

    /// <summary>
    /// The <c>COST</c> estimate as written, or null when unwritten. Held as text rather than
    /// a number because it is a planner hint reproduced verbatim, never computed with.
    /// </summary>
    public string? Cost { get; set; }

    /// <summary>The <c>ROWS</c> estimate as written, or null when unwritten.</summary>
    public string? Rows { get; set; }

    /// <summary>
    /// The <c>PARALLEL</c> safety level as written (<c>SAFE</c>, <c>RESTRICTED</c> or
    /// <c>UNSAFE</c>), or null when unwritten (PostgreSQL defaults to UNSAFE).
    /// </summary>
    public string? Parallel { get; set; }

    /// <summary>
    /// Whether <c>LEAKPROOF</c> was declared; false for <c>NOT LEAKPROOF</c> and null when
    /// neither was written (PostgreSQL defaults to not leakproof).
    /// </summary>
    public bool? Leakproof { get; set; }

    /// <summary>The <c>SUPPORT</c> planner-support function name, or null when unwritten.</summary>
    public string? SupportFunction { get; set; }

    /// <summary>
    /// Whether <c>WINDOW</c> was declared. A window function's implementation lives in a
    /// linked library, so it is parsed but cannot be modeled.
    /// </summary>
    public bool IsWindow { get; set; }

    /// <summary>The type names of a <c>TRANSFORM FOR TYPE ...</c> clause, in declaration order.</summary>
    public IList<string> TransformTypes { get; } = new List<string>();

    /// <summary>
    /// The link symbol of the two-string <c>AS 'obj_file', 'link_symbol'</c> form, which
    /// declares a function implemented in a linked C library. When this is set,
    /// <see cref="Body"/> holds the object file rather than a routine body.
    /// </summary>
    public string? LinkSymbol { get; set; }
}

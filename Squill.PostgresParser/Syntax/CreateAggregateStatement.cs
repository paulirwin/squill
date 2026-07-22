namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>CREATE AGGREGATE name(args) (SFUNC = ..., STYPE = ..., ...)</c> statement (issue #82).
/// An aggregate arrives via the <c>definestmt</c> grammar rule — the same rule that carries
/// <c>CREATE TYPE ... AS ENUM</c> — in its <c>CREATE [OR REPLACE] AGGREGATE func_name
/// aggr_args definition</c> alternative.
///
/// An aggregate's identity is its name and the input types it aggregates over; the two facets
/// Squill models beyond that are the mandatory <see cref="StateFunction"/> (<c>SFUNC</c>) and
/// <see cref="StateType"/> (<c>STYPE</c>), which every aggregate must declare. The aggregate
/// depends on its state function, so it must be created after that function.
///
/// The remaining definition items (FINALFUNC, INITCOND, etc.) are recognized but not modeled;
/// scope is deliberately the minimum needed to build Pagila's <c>group_concat</c>.
/// </summary>
public class CreateAggregateStatement : Statement
{
    public CreateAggregateStatement(QualifiedName name, bool orReplace)
    {
        Name = name;
        OrReplace = orReplace;
    }

    public QualifiedName Name { get; }

    /// <summary>
    /// Whether OR REPLACE was written. This affects how the aggregate is created, not the
    /// desired schema state, so it does not participate in the model.
    /// </summary>
    public bool OrReplace { get; }

    /// <summary>
    /// The aggregate's input parameters (the <c>aggr_args</c> list). An aggregate over
    /// <c>*</c> (e.g. <c>count(*)</c>) has none; ordered-set aggregates are not modeled.
    /// </summary>
    public IList<RoutineParameter> Parameters { get; } = new List<RoutineParameter>();

    /// <summary>The state transition function name (the <c>SFUNC</c> item), verbatim.</summary>
    public string? StateFunction { get; set; }

    /// <summary>The state (accumulator) type (the <c>STYPE</c> item).</summary>
    public DataType? StateType { get; set; }
}

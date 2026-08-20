namespace Squill.PostgresParser.Syntax;

/// <summary>The point in the statement's execution a trigger fires at.</summary>
public enum TriggerTiming
{
    Before,
    After,
    InsteadOf,
}

/// <summary>An event a trigger fires on. TRUNCATE is only valid on a statement-level trigger.</summary>
[Flags]
public enum TriggerEvents
{
    None = 0,
    Insert = 1,
    Delete = 2,
    Update = 4,
    Truncate = 8,
}

/// <summary>Whether a trigger fires once per affected row or once per statement.</summary>
public enum TriggerLevel
{
    Statement,
    Row,
}

/// <summary>
/// A <c>CREATE TRIGGER name { BEFORE | AFTER | INSTEAD OF } events ON table
/// FOR EACH { ROW | STATEMENT } EXECUTE { FUNCTION | PROCEDURE } func(args)</c> statement
/// (issue #83).
///
/// A trigger arrives via the <c>createtrigstmt</c> grammar rule. Squill models its identity
/// facets — name, the <see cref="Table"/> it is attached to, the <see cref="Timing"/>,
/// the OR'd <see cref="Events"/>, the <see cref="Level"/> — plus the function it runs
/// (<see cref="FunctionName"/> with <see cref="FunctionArguments"/>). A trigger depends on
/// both its table and, when it runs a user-defined function, on that function, so it is
/// created after both.
///
/// The declaration modifiers all change how often, or whether, the body runs, so each is
/// captured rather than dropped (issue #214): a <see cref="WhenCondition"/> gating predicate,
/// <see cref="UpdateOfColumns"/> restricting an UPDATE trigger to named columns,
/// <see cref="OldTransitionTable"/>/<see cref="NewTransitionTable"/> naming REFERENCING
/// transition tables, and the <c>CREATE CONSTRAINT TRIGGER</c> form with its deferrability.
/// </summary>
public class CreateTriggerStatement : Statement
{
    public CreateTriggerStatement(string name, QualifiedName table)
    {
        Name = name;
        Table = table;
    }

    /// <summary>The trigger's name. A trigger's name is scoped to its table, not a schema.</summary>
    public string Name { get; }

    /// <summary>The table the trigger is attached to.</summary>
    public QualifiedName Table { get; }

    public TriggerTiming Timing { get; set; }

    public TriggerEvents Events { get; set; }

    public TriggerLevel Level { get; set; }

    /// <summary>The (possibly schema-qualified) function the trigger executes.</summary>
    public QualifiedName? FunctionName { get; set; }

    /// <summary>
    /// The literal arguments passed to the trigger function, verbatim as written. Postgres
    /// passes these as strings; <c>tsvector_update_trigger('tsv', 'pg_catalog.english', ...)</c>
    /// is the motivating case.
    /// </summary>
    public IList<string> FunctionArguments { get; } = new List<string>();

    /// <summary>
    /// The <c>WHEN (...)</c> condition gating the trigger, or null when none was declared.
    /// </summary>
    /// <remarks>
    /// The predicate references the <c>NEW</c> and <c>OLD</c> pseudo-rows, which is what makes
    /// a qualified column reference reach a modeled construct at all: measured, PostgreSQL
    /// strips a table qualifier out of a CHECK predicate but keeps <c>new</c>/<c>old</c> here.
    /// </remarks>
    public Expression? WhenCondition { get; set; }

    /// <summary>
    /// The columns an <c>UPDATE OF</c> trigger is restricted to, in the order declared, empty
    /// when the trigger fires for an update to any column.
    /// </summary>
    /// <remarks>
    /// Order is kept rather than sorted: measured, PostgreSQL renders the list back in the
    /// order it was given, so sorting would rewrite the user's DDL.
    /// </remarks>
    public IList<Identifier> UpdateOfColumns { get; } = new List<Identifier>();

    /// <summary>
    /// The name given to the OLD transition table by <c>REFERENCING OLD TABLE AS ...</c>, or
    /// null when none was declared. A transition table is visible to the trigger body, so its
    /// name is part of the contract with the function.
    /// </summary>
    public string? OldTransitionTable { get; set; }

    /// <summary>The name given to the NEW transition table, or null when none was declared.</summary>
    public string? NewTransitionTable { get; set; }

    /// <summary>
    /// Whether this is a <c>CREATE CONSTRAINT TRIGGER</c>: a trigger that fires at
    /// constraint-check time and may therefore be deferred to the end of the transaction.
    /// </summary>
    public bool IsConstraintTrigger { get; set; }

    /// <summary>
    /// Whether the constraint trigger's firing may be deferred. Only meaningful on a
    /// constraint trigger; false otherwise, matching pg_trigger.tgdeferrable.
    /// </summary>
    public bool IsDeferrable { get; set; }

    /// <summary>
    /// Whether the constraint trigger defers by default. Implies <see cref="IsDeferrable"/>,
    /// as PostgreSQL rejects INITIALLY DEFERRED without DEFERRABLE.
    /// </summary>
    public bool IsInitiallyDeferred { get; set; }
}

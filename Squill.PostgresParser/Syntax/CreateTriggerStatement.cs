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
/// Not modeled: the <c>CREATE CONSTRAINT TRIGGER</c> form, <c>REFERENCING</c> transition
/// tables, a <c>WHEN (...)</c> condition, and <c>UPDATE OF column</c> — all reported as
/// unsupported rather than silently dropped. Scope is the minimum needed to build Pagila's
/// <c>last_updated</c> and <c>film_fulltext_trigger</c>.
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
}

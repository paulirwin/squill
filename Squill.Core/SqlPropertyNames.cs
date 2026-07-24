namespace Squill.Core;

/// <summary>
/// The <see cref="Property"/> keys shared across providers. Providers reference these
/// shared names (typically by forwarding their own constant to the value here) and add
/// only their provider-specific properties on top (e.g. Postgres identity/sequence and
/// operator-class options, MariaDB auto-increment and collection values).
/// </summary>
public abstract class SqlPropertyNames
{
    public const string IsNullable = nameof(IsNullable);
    public const string Length = nameof(Length);
    public const string Precision = nameof(Precision);
    public const string Scale = nameof(Scale);
    public const string IsUnique = nameof(IsUnique);
    public const string IndexMethod = nameof(IndexMethod);
    public const string IsAscending = nameof(IsAscending);
    public const string DeleteAction = nameof(DeleteAction);
    public const string UpdateAction = nameof(UpdateAction);
    // The canonical form of a column DEFAULT constant literal.
    public const string DefaultValue = nameof(DefaultValue);
    // A routine's argument signature and body (held verbatim).
    public const string Arguments = nameof(Arguments);
    public const string Body = nameof(Body);
    // A function's return type (engine-normalized).
    public const string ReturnType = nameof(ReturnType);
    // A view's query, carried for scripting only — it never takes part in comparison (every
    // engine rewrites the query it is given, so a declared body can never hash-match an
    // extracted one). A view's modeled identity is its name and column list instead.
    public const string Definition = nameof(Definition);
    // A trigger's timing (BEFORE/AFTER/...) and its bare (unscoped) name, recovered for
    // scripting from an element Name that folds in the table it fires on.
    public const string Timing = nameof(Timing);
    public const string RoutineName = nameof(RoutineName);
    // A CHECK constraint's predicate (and, on Postgres, a domain's). Carried for scripting
    // only — every engine rewrites the predicate it is given, so a declared expression can
    // never hash-match an extracted one; see ParticipatesInIdentity.
    public const string CheckExpression = nameof(CheckExpression);
    // A generated (computed) column's generation expression, and whether it is stored on
    // disk rather than computed on read (issue #120). Like a CHECK predicate the expression
    // is carried for scripting only and does not take part in comparison — every engine
    // rewrites the expression it is given, so a declared one can never hash-match an
    // extracted one. IsStored is false only for MariaDB's VIRTUAL columns; PostgreSQL
    // supports STORED alone.
    public const string GeneratedExpression = nameof(GeneratedExpression);
    public const string IsStored = nameof(IsStored);
}

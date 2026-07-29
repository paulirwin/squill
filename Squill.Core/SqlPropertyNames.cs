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
    // A CHECK constraint's predicate (and, on Postgres, a domain's), exactly as declared or as
    // the catalog reported it. Carried for scripting, so a deploy reproduces the spelling the
    // user wrote; comparison uses NormalizedCheckExpression instead, because every engine
    // rewrites the predicate it is given.
    public const string CheckExpression = nameof(CheckExpression);
    // A generated (computed) column's generation expression, and whether it is stored on
    // disk rather than computed on read (issue #120). As with a CHECK predicate the raw
    // expression is carried for scripting and NormalizedGeneratedExpression is what takes part
    // in comparison. IsStored is false only for MariaDB's VIRTUAL columns; PostgreSQL
    // supports STORED alone.
    public const string GeneratedExpression = nameof(GeneratedExpression);
    public const string IsStored = nameof(IsStored);

    // The canonical form of the two expressions above: what the expression reduces to once the
    // rewriting each engine applies when it stores it has been undone (issue #156). This is the
    // property that takes part in identity, which is what makes redefining a predicate under the
    // same name a change the deploy acts on — comparing the raw text could not, since a declared
    // predicate and the same predicate read back from the catalog are spelled differently.
    //
    // Absent when the expression contains a construct with no canonical form established by
    // measurement. The raw property then falls back to not taking part in identity, so a gap
    // degrades to the pre-#156 behaviour (a redefinition is missed) rather than to a false
    // "changed" that would redeploy the object on every deploy.
    public const string NormalizedCheckExpression = nameof(NormalizedCheckExpression);
    public const string NormalizedGeneratedExpression = nameof(NormalizedGeneratedExpression);
}

namespace Squill.Provider.MariaDb;

/// <summary>
/// The <see cref="Squill.Core.Property"/> keys for the MariaDB provider. A subset of the
/// Postgres property names — MariaDB models the same column/index facets but expresses
/// auto-increment (rather than Postgres identity) via <see cref="IsAutoIncrement"/>, and
/// has no operator-class or extension-version concept.
/// </summary>
public static class MariaDbPropertyNames
{
    public const string IsNullable = nameof(IsNullable);
    public const string Length = nameof(Length);
    public const string Precision = nameof(Precision);
    public const string Scale = nameof(Scale);
    public const string IsUnsigned = nameof(IsUnsigned);
    public const string IsUnique = nameof(IsUnique);
    public const string IndexMethod = nameof(IndexMethod);
    public const string IsAscending = nameof(IsAscending);
    public const string DeleteAction = nameof(DeleteAction);
    public const string UpdateAction = nameof(UpdateAction);
    public const string IsAutoIncrement = nameof(IsAutoIncrement);
    public const string DefaultValue = nameof(DefaultValue);

    // The parenthesized value list of an enum/set column, e.g. ('G','PG'). Stored verbatim
    // so it can be reproduced when scripting, and read identically from both the parser and
    // the DB extractor (information_schema.COLUMN_TYPE) so the two sides hash-match.
    public const string CollectionValues = nameof(CollectionValues);

    // Stored procedures (issue #41). Body is the routine source held verbatim, which is
    // what both engines return from information_schema.ROUTINES.ROUTINE_DEFINITION.
    // Unlike PostgreSQL there is no Language (SQL is the only one) and no ArgumentTypes
    // (neither engine allows overloading, so a routine's name alone identifies it).
    public const string Arguments = nameof(Arguments);
    public const string Body = nameof(Body);
    public const string IsDeterministic = nameof(IsDeterministic);
    public const string SqlDataAccess = nameof(SqlDataAccess);
    public const string IsSecurityInvoker = nameof(IsSecurityInvoker);

    // Views (issue #42). A view's query is carried for scripting only and never takes part
    // in comparison: MariaDB and MySQL both rewrite the query when they store it — and not
    // even the same way as each other — so a declared body could never hash-match an
    // extracted one. A view's modeled identity is its name and column list instead. Stored
    // as a property that opts out of the element's hash; see MariaDbModelFactory.CreateView.
    public const string Definition = nameof(Definition);
}

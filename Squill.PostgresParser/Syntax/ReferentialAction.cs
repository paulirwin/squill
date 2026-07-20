namespace Squill.PostgresParser.Syntax;

/// <summary>
/// The action taken on a referencing row when the referenced row is updated or
/// deleted (the <c>ON UPDATE</c> / <c>ON DELETE</c> clause of a foreign key).
/// </summary>
public enum ReferentialAction
{
    /// <summary>NO ACTION — the Postgres default; the reference is checked at end of statement.</summary>
    NoAction,
    Restrict,
    Cascade,
    SetNull,
    SetDefault,
}

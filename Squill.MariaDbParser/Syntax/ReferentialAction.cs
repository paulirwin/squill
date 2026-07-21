namespace Squill.MariaDbParser.Syntax;

/// <summary>
/// The action taken on a referencing row when the referenced row is updated or deleted
/// (the <c>ON UPDATE</c> / <c>ON DELETE</c> clause of a foreign key).
/// </summary>
public enum ReferentialAction
{
    /// <summary>RESTRICT — MariaDB's default (and how it reports NO ACTION).</summary>
    Restrict,
    Cascade,
    SetNull,
    SetDefault,
    NoAction,
}

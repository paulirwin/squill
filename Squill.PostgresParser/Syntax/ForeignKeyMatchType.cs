namespace Squill.PostgresParser.Syntax;

/// <summary>
/// The <c>MATCH</c> clause of a foreign key (issue #205), which decides how a composite key
/// containing NULLs is treated.
///
/// This is enforced semantics, not decoration: measured against PostgreSQL 18, a composite FK
/// declared <c>MATCH FULL</c> rejects the row <c>(1, NULL)</c> that the same FK declared
/// <c>MATCH SIMPLE</c> accepts. Dropping the clause therefore deployed a constraint that
/// accepted rows the source intended to reject.
/// </summary>
public enum ForeignKeyMatchType
{
    /// <summary>
    /// <c>MATCH SIMPLE</c> — the PostgreSQL default, and what an omitted clause means. A row is
    /// allowed when any referencing column is NULL.
    /// </summary>
    Simple,

    /// <summary>
    /// <c>MATCH FULL</c> — a composite key must be either entirely NULL or entirely non-NULL.
    /// </summary>
    Full,

    /// <summary>
    /// <c>MATCH PARTIAL</c> — parses, but PostgreSQL itself rejects it with "MATCH PARTIAL not
    /// yet implemented", so the provider refuses it rather than modeling something no server
    /// will accept.
    /// </summary>
    Partial,
}

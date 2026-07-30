namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>CREATE SCHEMA [IF NOT EXISTS] name</c> statement. Squill models a schema as a
/// declared, first-class object (like a table or extension), so the schema a table lives
/// in must be created explicitly rather than conjured implicitly at deploy time.
/// </summary>
public class CreateSchemaStatement : Statement
{
    public CreateSchemaStatement(Identifier name, bool ifNotExists)
    {
        Name = name;
        IfNotExists = ifNotExists;
    }

    public Identifier Name { get; }

    public bool IfNotExists { get; }

    /// <summary>
    /// The role named by an <c>AUTHORIZATION</c> clause, or null when omitted. Carried but not
    /// modeled: Squill does not manage roles, so ownership cannot be compared against the
    /// target and is reported as an unmodeled construct (issue #143). Note that in the
    /// name-less form (<c>CREATE SCHEMA AUTHORIZATION joe</c>) the schema takes its name from
    /// the role, so <see cref="Name"/> and this are the same identifier.
    ///
    /// <para>
    /// This may be the token <c>CURRENT_USER</c> or <c>SESSION_USER</c> rather than a role
    /// name, but only when the schema was named explicitly (issue #166) — the name-less form
    /// rejects those, since it would have to turn one into a schema name that is not knowable
    /// until deploy time. The token is kept as written so the SQ1002 warning can name exactly
    /// what was dropped; like a named role, it is not emitted in the generated DDL, which is a
    /// bare <c>CREATE SCHEMA IF NOT EXISTS</c>.
    /// </para>
    /// </summary>
    public string? Authorization { get; set; }
}

using Squill.Core;

namespace Squill.Provider.Postgres;

/// <summary>
/// The element <see cref="Element.Type"/> discriminators for the Postgres provider. Inherits
/// the shared <see cref="SqlElementTypes"/> vocabulary (SqlTable, SqlIndex, …) and adds the
/// Postgres-only object types on top.
/// </summary>
public sealed class PostgresElementTypes : SqlElementTypes
{
    public const string SqlExtension = nameof(SqlExtension);
    public const string SqlSchema = nameof(SqlSchema);
    // User-defined types (issue #75): a CREATE TYPE ... AS ENUM and a CREATE DOMAIN. Both are
    // top-level, standalone, declared objects a column's type may reference.
    public const string SqlEnumType = nameof(SqlEnumType);
    public const string SqlDomain = nameof(SqlDomain);
    // A CREATE AGGREGATE (issue #82). A user-defined aggregate function, recorded in
    // pg_aggregate (with prokind = 'a' in pg_proc). It references a state transition
    // function (SFUNC), so it must be created after that function.
    public const string SqlAggregate = nameof(SqlAggregate);
    // A standalone CREATE SEQUENCE (issue #122). Distinct from the sequence PostgreSQL
    // creates implicitly behind a serial or identity column, which belongs to that column
    // and is modeled as part of it — only an independently declared sequence is an element.
    public const string SqlSequence = nameof(SqlSequence);
}

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
    // CREATE TYPE beyond the AS ENUM form (issue #122). A composite type carries an ordered
    // attribute list (as a Columns relationship, the same shape a table uses); a range type
    // carries the subtype it is built over. Both are top-level declared objects a column may
    // be typed as. The row type PostgreSQL creates implicitly for every table is NOT one of
    // these — only an independently declared type is an element.
    public const string SqlCompositeType = nameof(SqlCompositeType);
    public const string SqlRangeType = nameof(SqlRangeType);
    // A CREATE COLLATION (issue #159). A top-level declared object a column's COLLATE may
    // reference, so it must be created before any column that names it. PostgreSQL resolves the
    // declared items into catalog facets and keeps no record of how they were written — a
    // collation copied FROM another is stored identically to one spelling out that collation's
    // locale — so the element carries the resolved facets, not the source spelling.
    public const string SqlCollation = nameof(SqlCollation);
}

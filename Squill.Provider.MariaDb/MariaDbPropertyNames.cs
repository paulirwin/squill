using Squill.Core;

namespace Squill.Provider.MariaDb;

/// <summary>
/// The <see cref="Squill.Core.Property"/> keys for the MariaDB provider. Inherits the shared
/// <see cref="SqlPropertyNames"/> vocabulary (the column/index/routine/view/trigger facets
/// MariaDB shares with Postgres) and adds the MariaDB-only properties: auto-increment (rather
/// than Postgres identity), unsigned integers, enum/set collection values, and the routine
/// characteristics MariaDB exposes.
/// </summary>
public sealed class MariaDbPropertyNames : SqlPropertyNames
{
    public const string IsUnsigned = nameof(IsUnsigned);
    public const string IsAutoIncrement = nameof(IsAutoIncrement);

    // The parenthesized value list of an enum/set column, e.g. ('G','PG'). Stored verbatim
    // so it can be reproduced when scripting, and read identically from both the parser and
    // the DB extractor (information_schema.COLUMN_TYPE) so the two sides hash-match.
    public const string CollectionValues = nameof(CollectionValues);

    // Stored procedures (issue #41) / functions (issue #74). Unlike PostgreSQL there is no
    // Language (SQL is the only one) and no ArgumentTypes (neither engine allows overloading,
    // so a routine's name alone identifies it). These are the extra routine characteristics
    // MariaDB records in information_schema.ROUTINES.
    public const string IsDeterministic = nameof(IsDeterministic);
    public const string SqlDataAccess = nameof(SqlDataAccess);
    public const string IsSecurityInvoker = nameof(IsSecurityInvoker);

    // Triggers (issue #100). A trigger fires at a Timing (shared) for an Event
    // (INSERT/UPDATE/DELETE) — which is what both engines return from
    // information_schema.TRIGGERS (EVENT_MANIPULATION).
    public const string Event = nameof(Event);
}

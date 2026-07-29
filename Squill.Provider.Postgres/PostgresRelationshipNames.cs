using Squill.Core;

namespace Squill.Provider.Postgres;

/// <summary>
/// The <see cref="Relationship"/> names for the Postgres provider. Inherits the shared
/// <see cref="SqlRelationshipNames"/> vocabulary and adds the Postgres-only schema
/// relationship (Postgres objects are schema-scoped).
/// </summary>
public sealed class PostgresRelationshipNames : SqlRelationshipNames
{
    public const string Schema = nameof(Schema);

    /// <summary>
    /// The <c>INCLUDE (...)</c> covering columns of an index (issue #160). Held apart from
    /// <see cref="SqlRelationshipNames.ColumnSpecifications"/> because these columns are stored
    /// in the index without being part of its key — they carry no ordering, direction or
    /// operator class, and contribute nothing to a unique index's uniqueness.
    /// </summary>
    public const string IncludedColumns = nameof(IncludedColumns);
}

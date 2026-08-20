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

    /// <summary>
    /// The <c>key WITH operator</c> pairs of an EXCLUDE constraint (issue #212), in
    /// declaration order.
    ///
    /// Held apart from <see cref="SqlRelationshipNames.ColumnSpecifications"/> because an
    /// exclusion element is a key <em>and</em> the operator that key is compared with, so it
    /// is one level deeper than a bare indexed-column specification. The order is significant:
    /// it must line up with the operator order PostgreSQL records in
    /// <c>pg_constraint.conexclop</c>.
    /// </summary>
    public const string ExclusionElements = nameof(ExclusionElements);
}

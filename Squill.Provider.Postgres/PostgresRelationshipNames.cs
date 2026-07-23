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
}

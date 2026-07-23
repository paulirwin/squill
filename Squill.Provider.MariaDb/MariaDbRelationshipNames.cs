using Squill.Core;

namespace Squill.Provider.MariaDb;

/// <summary>
/// The <see cref="Squill.Core.Relationship"/> names for the MariaDB provider. MariaDB uses
/// exactly the shared <see cref="SqlRelationshipNames"/> vocabulary — including the trigger
/// table (issue #100) — minus the schema relationship (MariaDB objects are not schema-scoped
/// within a database), so it adds nothing of its own. The type exists so provider code can
/// refer to <c>MariaDbRelationshipNames.Columns</c> symmetrically with the Postgres provider.
/// </summary>
public sealed class MariaDbRelationshipNames : SqlRelationshipNames;

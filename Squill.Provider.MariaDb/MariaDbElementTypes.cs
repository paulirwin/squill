using Squill.Core;

namespace Squill.Provider.MariaDb;

/// <summary>
/// The element <see cref="Squill.Core.Element.Type"/> discriminators for the MariaDB
/// provider. MariaDB inherits the shared <see cref="SqlElementTypes"/> vocabulary and adds
/// only the scheduled-event type: it has no Postgres-only schema / extension / enum / domain
/// / aggregate types, since a MariaDB "schema" is the database itself and it has no extension
/// concept. The type also lets provider code refer to <c>MariaDbElementTypes.SqlTable</c>
/// symmetrically with the Postgres provider.
/// </summary>
public sealed class MariaDbElementTypes : SqlElementTypes
{
    // A CREATE EVENT (issue #122): a routine run on a schedule rather than in response to a
    // table change. MariaDB/MySQL-only — PostgreSQL has no in-server scheduler — so unlike
    // SqlTrigger it is not part of the shared vocabulary.
    public const string SqlEvent = nameof(SqlEvent);
}

using Squill.Core;

namespace Squill.Provider.MariaDb;

/// <summary>
/// The element <see cref="Squill.Core.Element.Type"/> discriminators for the MariaDB
/// provider. MariaDB models exactly the shared <see cref="SqlElementTypes"/> vocabulary and
/// adds nothing of its own: it has no Postgres-only schema / extension / enum / domain /
/// aggregate types, since a MariaDB "schema" is the database itself and it has no extension
/// concept. The type exists so provider code can refer to <c>MariaDbElementTypes.SqlTable</c>
/// symmetrically with the Postgres provider.
/// </summary>
public sealed class MariaDbElementTypes : SqlElementTypes;

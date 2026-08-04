using Squill.Dacpac;

namespace Squill.Provider.Postgres;

/// <summary>
/// The schema provider for PostgreSQL 17. Discovered by reflection and recorded in a DACPAC
/// (via its <see cref="Squill.Dacpac.DatabaseSchemaProvider.DspName"/>) when a project targets
/// this version.
/// </summary>
public sealed class Postgresql17DatabaseSchemaProvider : PostgresqlDatabaseSchemaProvider
{
    /// <summary>The canonical instance, discovered by reflection and cached by the registry.</summary>
    public Postgresql17DatabaseSchemaProvider()
    {
    }

    /// <summary>
    /// A per-build instance carrying the declared target floor, so a project that names a point
    /// release (PostgreSQL 17.x.y) gates against what it actually declared rather than this major's
    /// oldest release. The registry finds this constructor by reflection and silently falls back
    /// to the unversioned instance when it is absent, so its absence is invisible at compile time.
    /// </summary>
    public Postgresql17DatabaseSchemaProvider(TargetVersion? targetVersion)
        : base(targetVersion)
    {
    }

    public override int MajorVersion => 17;

    // SET EXPRESSION arrived in PostgreSQL 17.
    public override bool SupportsSetExpression => true;
}

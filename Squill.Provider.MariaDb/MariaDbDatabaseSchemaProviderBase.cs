using Squill.Dacpac;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Base for the per-major-version MariaDB schema providers. Fixes the
/// <see cref="DatabaseSchemaProvider.ProviderName"/> to <c>MariaDb</c> so each concrete version
/// subclass need only declare its <see cref="DatabaseSchemaProvider.MajorVersion"/>. Abstract,
/// so it is not itself discovered; the concrete subclasses remain distinct types whose full
/// names are recorded in DACPACs.
/// </summary>
public abstract class MariaDbDatabaseSchemaProviderBase : MariaDbFamilyDatabaseSchemaProvider
{
    protected MariaDbDatabaseSchemaProviderBase()
    {
    }

    protected MariaDbDatabaseSchemaProviderBase(TargetVersion? targetVersion)
        : base(targetVersion)
    {
    }

    public override string ProviderName => "MariaDb";

    public override bool IsMySql => false;

    // MariaDB keeps these as separate functions rather than folding them into
    // current_timestamp(): measured, DEFAULT LOCALTIME is stored as curtime() — a time of day —
    // and LOCALTIMESTAMP as localtimestamp().
    public override bool LocalTimeIsCurrentTimestampSynonym => false;

    // CURDATE()/CURTIME() (and their CURRENT_DATE/CURRENT_TIME keyword spellings) are accepted
    // as column defaults here, each stored under its own name.
    public override bool SupportsDateAndTimeFunctionDefaults => true;

    // Sequences are a MariaDB feature (10.3+); every supported major here has them.
    public override bool SupportsSequences => true;

    // MariaDB has no functional indexes: the DDL is a syntax error, and STATISTICS has no
    // EXPRESSION column to read one back from.
    public override bool SupportsFunctionalIndexKeys => false;

    // MariaDB spells this IGNORED / NOT IGNORED and reports STATISTICS.IGNORED. Measured, it
    // rejects MySQL's INVISIBLE with a syntax error and has no IS_VISIBLE column.
    // https://mariadb.com/kb/en/ignored-indexes/
    public override IndexVisibilityStyle? IndexVisibility => IndexVisibilityStyle.Ignored;
}

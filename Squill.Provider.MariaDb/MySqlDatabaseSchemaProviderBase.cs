using Squill.Dacpac;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Base for the per-major-version MySQL schema providers. Served by the MariaDB provider
/// assembly (one provider covers both engines). Fixes the
/// <see cref="DatabaseSchemaProvider.ProviderName"/> to <c>MySql</c> so each concrete version
/// subclass need only declare its <see cref="DatabaseSchemaProvider.MajorVersion"/>. Abstract,
/// so it is not itself discovered; the concrete subclasses remain distinct types whose full
/// names are recorded in DACPACs.
/// </summary>
public abstract class MySqlDatabaseSchemaProviderBase : MariaDbFamilyDatabaseSchemaProvider
{
    protected MySqlDatabaseSchemaProviderBase()
    {
    }

    protected MySqlDatabaseSchemaProviderBase(TargetVersion? targetVersion)
        : base(targetVersion)
    {
    }

    public override string ProviderName => "MySql";

    public override bool IsMySql => true;

    // Measured, and matching MySQL's documented behaviour: LOCALTIME / LOCALTIMESTAMP are true
    // CURRENT_TIMESTAMP synonyms here and are reported as CURRENT_TIMESTAMP.
    // https://dev.mysql.com/doc/refman/8.4/en/timestamp-initialization.html
    public override bool LocalTimeIsCurrentTimestampSynonym => true;

    // CURDATE()/CURTIME() are a syntax error in a DEFAULT on MySQL, so they are never modeled.
    public override bool SupportsDateAndTimeFunctionDefaults => false;

    // Functional index keys exist here (8.0.13+) and are reported in STATISTICS.EXPRESSION.
    // https://dev.mysql.com/doc/refman/8.4/en/create-index.html#create-index-functional-key-parts
    public override bool SupportsFunctionalIndexKeys => true;

    // ... but only from 8.0.13, so a project whose floor predates that cannot author one even
    // though its major can. This is the build-time half of the question; the capability above
    // stays engine-level because extraction resolves it from the live server (issue #189).
    public override bool CanAuthorFunctionalIndexKeys => SupportsFeatureFrom(8, 0, 13);
}

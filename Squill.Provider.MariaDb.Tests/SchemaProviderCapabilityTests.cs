using Squill.Dacpac;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Pins where engine capabilities live, and what each engine declares.
///
/// The capabilities that differ between MariaDB and MySQL belong on
/// <see cref="MariaDbFamilyDatabaseSchemaProvider"/> — the base shared by just those two —
/// rather than on the universal <see cref="DatabaseSchemaProvider"/>. A Postgres provider must
/// not inherit questions that have no answer for it: Postgres canonicalizes column defaults on
/// entirely different rules, and none of these tokens are part of its model at all.
///
/// Code that varies by engine reads these properties. It does not test for a provider type or
/// compare a provider-name string, so adding an engine means declaring its answers here rather
/// than hunting for branches elsewhere.
/// </summary>
public class SchemaProviderCapabilityTests
{
    /// <summary>
    /// Measured against <c>mariadb:latest</c>: LOCALTIME / LOCALTIMESTAMP are distinct
    /// functions, each with its own stored form, so they never fold into CURRENT_TIMESTAMP.
    /// CURDATE / CURTIME are accepted as defaults.
    /// </summary>
    [Fact]
    public void MariaDb_DeclaresItsMeasuredCapabilities()
    {
        var provider = new MariaDb12DatabaseSchemaProvider();

        Assert.False(provider.LocalTimeIsCurrentTimestampSynonym);
        Assert.True(provider.SupportsDateAndTimeFunctionDefaults);
    }

    /// <summary>
    /// Measured against <c>mysql:latest</c>, and matching MySQL's documented behaviour:
    /// LOCALTIME / LOCALTIMESTAMP <em>are</em> CURRENT_TIMESTAMP synonyms, while CURDATE() /
    /// CURTIME() are a syntax error in a DEFAULT.
    /// </summary>
    [Fact]
    public void MySql_DeclaresItsMeasuredCapabilities()
    {
        var provider = new MySql9DatabaseSchemaProvider();

        Assert.True(provider.LocalTimeIsCurrentTimestampSynonym);
        Assert.False(provider.SupportsDateAndTimeFunctionDefaults);
    }

    /// <summary>
    /// The capabilities are engine-wide, not per-major: every supported major of an engine must
    /// answer identically, or a project pinning an older target would silently model differently
    /// from one that does not pin.
    /// </summary>
    [Fact]
    public void EveryMajorOfAnEngine_AgreesOnCapabilities()
    {
        var byEngine = DatabaseSchemaProviderRegistry.All
            .OfType<MariaDbFamilyDatabaseSchemaProvider>()
            .GroupBy(p => p.ProviderName, StringComparer.OrdinalIgnoreCase);

        foreach (var engine in byEngine)
        {
            Assert.Single(engine.Select(p => p.LocalTimeIsCurrentTimestampSynonym).Distinct());
            Assert.Single(engine.Select(p => p.SupportsDateAndTimeFunctionDefaults).Distinct());
        }
    }

    /// <summary>
    /// These capabilities are specific to the MariaDB/MySQL family and must not leak onto the
    /// universal base, where every other engine's provider would inherit them.
    /// </summary>
    [Fact]
    public void Capabilities_AreNotOnTheUniversalBase()
    {
        var universal = typeof(DatabaseSchemaProvider);

        Assert.Null(universal.GetProperty(
            nameof(MariaDbFamilyDatabaseSchemaProvider.LocalTimeIsCurrentTimestampSynonym)));
        Assert.Null(universal.GetProperty(
            nameof(MariaDbFamilyDatabaseSchemaProvider.SupportsDateAndTimeFunctionDefaults)));
    }

    /// <summary>
    /// Both engines this provider serves derive from the shared family base, so a capability
    /// added there is a question both must answer.
    /// </summary>
    [Fact]
    public void BothEngines_DeriveFromTheFamilyBase()
    {
        Assert.IsAssignableFrom<MariaDbFamilyDatabaseSchemaProvider>(
            new MariaDb12DatabaseSchemaProvider());
        Assert.IsAssignableFrom<MariaDbFamilyDatabaseSchemaProvider>(
            new MySql9DatabaseSchemaProvider());
    }
}

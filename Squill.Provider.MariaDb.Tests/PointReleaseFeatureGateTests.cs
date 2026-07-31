using Squill.Dacpac;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Covers the build-time point-release gate (issue #189): whether a construct is available in
/// <em>every</em> release at or after the declared floor.
/// </summary>
public class PointReleaseFeatureGateTests
{
    private static MariaDbFamilyDatabaseSchemaProvider MySqlTargeting(string targetVersion)
        => (MariaDbFamilyDatabaseSchemaProvider)DatabaseSchemaProviderRegistry.Resolve(
            "MySql", TargetVersion.Parse(targetVersion));

    /// <summary>
    /// The behaviour change the issue flags for release notes. MySQL gained functional index key
    /// parts in 8.0.13, but the provider answered an unconditional <c>true</c> for all of major 8
    /// — so a project targeting a bare <c>8</c> (which means 8.0.0) was told a construct was safe
    /// that its declared floor does not have.
    /// </summary>
    [Theory]
    [InlineData("8")]
    [InlineData("8.0")]
    [InlineData("8.0.0")]
    [InlineData("8.0.12")]
    public void FunctionalIndexKeys_AreNotAuthorableBelowTheIntroducingPatch(string floor)
    {
        Assert.False(MySqlTargeting(floor).CanAuthorFunctionalIndexKeys);
    }

    [Theory]
    [InlineData("8.0.13")]
    [InlineData("8.0.14")]
    [InlineData("8.4")]
    [InlineData("9.0")]
    public void FunctionalIndexKeys_AreAuthorableAtOrAfterTheIntroducingPatch(string floor)
    {
        Assert.True(MySqlTargeting(floor).CanAuthorFunctionalIndexKeys);
    }

    /// <summary>
    /// MariaDB has no functional indexes at any version, so the engine-level capability still
    /// decides regardless of how new the declared floor is.
    /// </summary>
    [Theory]
    [InlineData("10.5")]
    [InlineData("11.4")]
    [InlineData("12.0")]
    public void MariaDb_NeverAuthorsFunctionalIndexKeys(string floor)
    {
        var provider = (MariaDbFamilyDatabaseSchemaProvider)DatabaseSchemaProviderRegistry.Resolve(
            "MariaDb", TargetVersion.Parse(floor));

        Assert.False(provider.CanAuthorFunctionalIndexKeys);
    }

    /// <summary>
    /// The extraction-time capability must keep answering for the <em>engine</em>, not the
    /// declared floor. It selects <c>STATISTICS.EXPRESSION</c> in the catalog query, and MariaDB
    /// has no such column — so letting a project's floor drive it would turn a build-time warning
    /// into an unknown-column failure, or silently stop extracting functional indexes from a
    /// server that has them.
    /// </summary>
    [Theory]
    [InlineData("8")]
    [InlineData("8.0.0")]
    [InlineData("8.4")]
    public void ExtractionCapability_IgnoresTheDeclaredFloor(string floor)
    {
        Assert.True(MySqlTargeting(floor).SupportsFunctionalIndexKeys);
    }

    [Fact]
    public void SupportsFeatureFrom_ComparesTheWholeFloor()
    {
        var provider = MySqlTargeting("8.0.16");

        Assert.True(provider.SupportsFeatureFrom(8, 0, 13));
        Assert.True(provider.SupportsFeatureFrom(8, 0, 16));
        Assert.False(provider.SupportsFeatureFrom(8, 0, 17));
        Assert.False(provider.SupportsFeatureFrom(8, 4));
        Assert.False(provider.SupportsFeatureFrom(9, 0));
    }

    /// <summary>
    /// A bare major carries no per-build state, so the registry hands back its cached singleton.
    /// That instance must still gate correctly, at its major's oldest release.
    /// </summary>
    [Fact]
    public void ABareMajor_GatesAtTheMajorsOldestRelease()
    {
        var provider = MySqlTargeting("8");

        Assert.Null(provider.TargetVersion);
        Assert.Equal(new TargetVersion(8, 0, 0), provider.Floor);
        Assert.False(provider.SupportsFeatureFrom(8, 0, 13));
    }

    /// <summary>
    /// Resolving with a floor must not write onto the registry's cached singleton. That instance
    /// is handed to every caller in the process, so a mutation would leak one project's target
    /// into unrelated builds — and because capabilities also shape catalog SQL, the symptom would
    /// be an extraction failure rather than merely a wrong warning.
    /// </summary>
    [Fact]
    public void ResolvingWithAFloor_LeavesTheCachedSingletonUntouched()
    {
        var canonical = DatabaseSchemaProviderRegistry.Resolve("MySql", 8);

        var targeted = MySqlTargeting("8.0.13");

        Assert.NotSame(canonical, targeted);
        Assert.Null(canonical.TargetVersion);
        Assert.Equal(new TargetVersion(8, 0, 13), targeted.TargetVersion);

        // The canonical instance the registry keeps handing out is still the untargeted one.
        Assert.Same(canonical, DatabaseSchemaProviderRegistry.Resolve("MySql", 8));
        Assert.Null(DatabaseSchemaProviderRegistry.Resolve("MySql", 8).TargetVersion);
    }

    /// <summary>
    /// Two builds resolving different floors concurrently must not see each other's target. This
    /// is the concrete failure a settable property would cause.
    /// </summary>
    [Fact]
    public void DifferentFloors_ResolveToIndependentInstances()
    {
        var oldFloor = MySqlTargeting("8.0.0");
        var newFloor = MySqlTargeting("8.0.23");

        Assert.NotSame(oldFloor, newFloor);
        Assert.False(oldFloor.CanAuthorFunctionalIndexKeys);
        Assert.True(newFloor.CanAuthorFunctionalIndexKeys);
    }

    /// <summary>
    /// The build path must hand the schema provider the <em>whole</em> declared target, not just
    /// its major. Resolving on the major alone would leave every gate reading a bare-major floor,
    /// so the plumbing would look correct while the feature did nothing.
    /// </summary>
    [Theory]
    [InlineData("8.0.13", true)]
    [InlineData("8.0.0", false)]
    public void TheBuildPath_CarriesTheDeclaredFloorToTheSchemaProvider(
        string declared, bool expectAuthorable)
    {
        var metadata = new ModelMetadata
        {
            ProviderName = "MySql",
            TargetVersion = TargetVersion.Parse(declared),
        };

        var provider = DacpacBuilder.SchemaProviderFor(
            metadata.ProviderName, metadata.TargetVersion);

        // Asserted on Floor rather than TargetVersion: a floor at the major's oldest release
        // needs no per-build state, so the registry hands back its cached singleton (whose
        // TargetVersion is null) instead of allocating an identical copy. Floor is what the gates
        // actually read, and it is right either way.
        Assert.Equal(TargetVersion.Parse(declared), provider.Floor);
        Assert.Equal(expectAuthorable, provider.CanAuthorFunctionalIndexKeys);
    }
}

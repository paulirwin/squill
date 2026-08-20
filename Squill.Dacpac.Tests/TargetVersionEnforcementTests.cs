using Squill.Core;

namespace Squill.Dacpac.Tests;

/// <summary>
/// Covers the deploy-time version gate (issue #189). The target recorded in a DACPAC is a
/// <em>floor</em> with no ceiling, so the check is deliberately one-sided: a server at or above
/// the floor deploys, however far above, and only an older one is refused.
///
/// <para>
/// The asymmetry is the point of carrying a minor at all, so it is asserted in both directions
/// rather than only on the failing side — a gate that rejected 8.4-on-8.0 but also rejected
/// 8.0-on-8.4 would pass a one-sided test while breaking every project targeting an old floor.
/// </para>
/// </summary>
public class TargetVersionEnforcementTests
{
    /// <summary>
    /// Exposes the protected gate on <see cref="DacpacDeployerBase"/>. The rest of the deploy
    /// pipeline needs a live server; the version check is pure logic and is tested on its own.
    /// </summary>
    private sealed class TestDeployer : DacpacDeployerBase
    {
        public void Check(ModelMetadata metadata, TargetVersion serverVersion)
            => EnforceTargetVersion(metadata, serverVersion);

        protected override IDatabaseProvider CreateProvider(string connectionString)
            => throw new NotSupportedException();

        protected override IDatabase CreateDatabase(string connectionString, string databaseName)
            => throw new NotSupportedException();

        protected override TargetVersion GetServerVersion(IDatabase database)
            => throw new NotSupportedException();

        protected override IScriptGenerator CreateScriptGenerator(ModelMetadata metadata)
            => throw new NotSupportedException();

        protected override string GetEngineName(ModelMetadata metadata) => "MySQL";

        protected override string ResolveDatabaseName(string connectionString)
            => throw new NotSupportedException();
    }

    private static ModelMetadata Targeting(TargetVersion? version)
        => new() { ProviderName = "MySql", TargetVersion = version };

    /// <summary>
    /// The case that is currently ungateable in principle: 8.0 and 8.4 are the same major, so
    /// without a minor this deploy would be allowed and fail (or silently misbehave) later.
    /// </summary>
    [Fact]
    public void Deploy_TargetingANewerMinor_IsRefusedOnAnOlderServer()
    {
        var deployer = new TestDeployer();

        var ex = Assert.Throws<TargetVersionMismatchException>(
            () => deployer.Check(Targeting(new TargetVersion(8, 4)), new TargetVersion(8, 0)));

        Assert.Equal(8, ex.RequiredMajorVersion);
        Assert.Equal(4, ex.RequiredMinorVersion);
        Assert.Equal(8, ex.ActualMajorVersion);
        Assert.Equal(0, ex.ActualMinorVersion);
        Assert.Equal("MySQL", ex.EngineName);
    }

    /// <summary>
    /// The other half of the asymmetry, and the reason an unspecified minor resolves to
    /// <c>.0</c>: a project declaring an old floor must keep deploying to newer servers.
    /// </summary>
    [Fact]
    public void Deploy_TargetingAnOlderMinor_IsAllowedOnANewerServer()
    {
        var deployer = new TestDeployer();

        deployer.Check(Targeting(new TargetVersion(8, 0)), new TargetVersion(8, 4));
    }

    [Theory]
    // Same version exactly: a floor is satisfied by its own value.
    [InlineData(8, 0, 8, 0)]
    [InlineData(8, 4, 8, 4)]
    // A floor is unbounded above, so later majors always clear it.
    [InlineData(8, 0, 9, 0)]
    [InlineData(8, 4, 9, 0)]
    // ... including when the older major carries the higher minor.
    [InlineData(8, 99, 9, 0)]
    public void Deploy_AgainstAServerAtOrAboveTheFloor_IsAllowed(
        int requiredMajor, int requiredMinor, int serverMajor, int serverMinor)
    {
        var deployer = new TestDeployer();

        deployer.Check(
            Targeting(new TargetVersion(requiredMajor, requiredMinor)),
            new TargetVersion(serverMajor, serverMinor));
    }

    [Theory]
    [InlineData(8, 4, 8, 0)]
    [InlineData(9, 0, 8, 4)]
    [InlineData(9, 0, 8, 99)]
    [InlineData(10, 11, 10, 5)]
    public void Deploy_AgainstAServerBelowTheFloor_IsRefused(
        int requiredMajor, int requiredMinor, int serverMajor, int serverMinor)
    {
        var deployer = new TestDeployer();

        Assert.Throws<TargetVersionMismatchException>(
            () => deployer.Check(
                Targeting(new TargetVersion(requiredMajor, requiredMinor)),
                new TargetVersion(serverMajor, serverMinor)));
    }

    /// <summary>
    /// The message must name the floor the author actually declared. Reporting an 8.0.13 target
    /// as "8.0" would point them at a version that does not have the feature they used.
    /// </summary>
    [Fact]
    public void Deploy_RefusedOnAPatchFloor_NamesTheWholeVersion()
    {
        var deployer = new TestDeployer();

        var ex = Assert.Throws<TargetVersionMismatchException>(
            () => deployer.Check(
                Targeting(new TargetVersion(8, 0, 13)), new TargetVersion(8, 0, 3)));

        Assert.Equal("8.0.13", ex.RequiredVersion);
        Assert.Equal("8.0.3", ex.ActualVersion);
        Assert.Contains("8.0.13", ex.Message, StringComparison.Ordinal);
        Assert.Contains("8.0.3", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    // A patch floor is refused by anything below it, including the same major.minor.
    [InlineData(8, 0, 13, 8, 0, 12)]
    [InlineData(8, 0, 16, 8, 0, 13)]
    public void Deploy_AgainstAServerBelowAPatchFloor_IsRefused(
        int rMajor, int rMinor, int rPatch, int sMajor, int sMinor, int sPatch)
    {
        var deployer = new TestDeployer();

        Assert.Throws<TargetVersionMismatchException>(
            () => deployer.Check(
                Targeting(new TargetVersion(rMajor, rMinor, rPatch)),
                new TargetVersion(sMajor, sMinor, sPatch)));
    }

    [Theory]
    [InlineData(8, 0, 13, 8, 0, 13)]
    [InlineData(8, 0, 13, 8, 0, 36)]
    [InlineData(8, 0, 13, 8, 4, 0)]
    public void Deploy_AgainstAServerAtOrAboveAPatchFloor_IsAllowed(
        int rMajor, int rMinor, int rPatch, int sMajor, int sMinor, int sPatch)
    {
        var deployer = new TestDeployer();

        deployer.Check(
            Targeting(new TargetVersion(rMajor, rMinor, rPatch)),
            new TargetVersion(sMajor, sMinor, sPatch));
    }

    [Fact]
    public void Deploy_WithNoRecordedTarget_IsUnconstrained()
    {
        var deployer = new TestDeployer();

        // An unconstrained package deploys anywhere, including to a server older than any
        // version this build ships a provider for.
        deployer.Check(Targeting(null), new TargetVersion(5, 5));
    }

    /// <summary>
    /// A bare major recorded as a floor of <c>.0</c> must not start refusing point releases of
    /// its own major, which is the regression the floor rule exists to prevent.
    /// </summary>
    [Fact]
    public void Deploy_TargetingABareMajor_IsAllowedOnAnyPointReleaseOfThatMajor()
    {
        var deployer = new TestDeployer();
        var metadata = Targeting(null);
        metadata.TargetMajorVersion = 8;

        deployer.Check(metadata, new TargetVersion(8, 0));
        deployer.Check(metadata, new TargetVersion(8, 4));
        deployer.Check(metadata, new TargetVersion(8, 40));
    }
}

using Squill.Core;
using Squill.MariaDbParser;
using Squill.Provider.MariaDb;
using Squill.TestFramework;

namespace Squill.IntegrationTests.MariaDb;

/// <summary>
/// Verifies the build-time deprecation warning added for issue #190 against real servers.
///
/// <para>
/// The claim SQ1006 makes is different from SQ1003's, and so is what has to be proved. A
/// target-version warning is proved by the server <em>rejecting</em> the DDL; a deprecated
/// construct is by definition still accepted, so the assertion here is the opposite one — the
/// same source that warns must still deploy, and still round-trip. A warning that fired on
/// something the engine had already removed would be an SQ1003, and one that broke the round-trip
/// would be reporting a modeling bug under a diagnostic that promises the object deploys fine.
/// </para>
///
/// <para>
/// The <c>utf8</c> case earns an integration test in its own right, which the others do not. Its
/// stored spelling moves underneath us: "Prior to MySQL 8.0.29, instances of utf8mb3 in statements
/// were converted to utf8. In MySQL 8.0.30 and later, the reverse is true"
/// (https://dev.mysql.com/doc/refman/8.0/en/charset-unicode-utf8mb3.html), so what
/// <c>SHOW CREATE TABLE</c> reports for a column declared <c>utf8</c> depends on the point release.
/// A canonicalization that guessed at that would re-diff on every deploy against half the servers
/// in the supported window, and only a live server can settle which spelling comes back.
/// </para>
/// </summary>
public abstract class MariaDbDeprecatedFeatureTests
{
    protected abstract MariaDbLikeFixture Fixture { get; }

    private async Task<BuildResult> BuildAsync(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("T.sql", FileKind.Compile, sql));

        return await new ParserWorkspaceModelBuilder(
                workspace, new AntlrMariaDbParser(), Fixture.SchemaProviderOf())
            .ExtractModelAsync(TestContext.Current.CancellationToken);
    }

    private async Task AssertRoundTripsAsync(string sql)
        => await RoundTripHarness.AssertRoundTripAsync(
            new MariaDbDatabaseProvider(Fixture.ConnectionString),
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrMariaDbParser(), Fixture.SchemaProviderOf()),
            sql,
            Fixture.EngineName,
            assertRedeployNoOp: true,
            TestContext.Current.CancellationToken);

    /// <summary>
    /// The core promise of SQ1006, asserted against a live server: the construct is deprecated,
    /// not removed, so the source it warns about still deploys and still round-trips. The
    /// assertion runs on both engines, only one of which warns — which is the point, since a
    /// deprecation that changed what Squill modeled would make the two engines disagree about a
    /// schema they both accept.
    /// </summary>
    [Theory]
    [InlineData("CREATE TABLE dep_zerofill (c INT ZEROFILL);")]
    [InlineData("CREATE TABLE dep_width (c INT(11));")]
    [InlineData("CREATE TABLE dep_unsigned (c DOUBLE UNSIGNED);")]
    public async Task DeprecatedConstruct_StillDeploysAndRoundTrips(string sql)
        => await AssertRoundTripsAsync(sql);

    /// <summary>
    /// The <c>utf8</c> alias round-trip (issue #190). Both engines resolve the alias to a concrete
    /// character set on their own terms — MySQL to the deprecated <c>utf8mb3</c>, MariaDB to
    /// whatever <c>old_mode</c> says — and the spelling that comes back out is not necessarily the
    /// one that went in. If Squill modeled the declared spelling rather than the stored one, this
    /// would diff on every deploy forever.
    /// </summary>
    [Fact]
    public async Task Utf8CharacterSet_RoundTrips()
        => await AssertRoundTripsAsync(
            "CREATE TABLE dep_utf8 (c VARCHAR(10) CHARACTER SET utf8);");

    /// <summary>
    /// And the same column declared with the spelling the engine actually stores, which is the
    /// other half of the flip: whichever of the two spellings a given point release reports, one
    /// of these two tests is the one crossing it.
    /// </summary>
    [Fact]
    public async Task Utf8mb3CharacterSet_RoundTrips()
        => await AssertRoundTripsAsync(
            "CREATE TABLE dep_utf8mb3 (c VARCHAR(10) CHARACTER SET utf8mb3);");

    /// <summary>
    /// The warning itself, against whichever engine the fixture is running. MySQL deprecates the
    /// construct and must report it; MariaDB documents it as current functionality and must stay
    /// silent. Asserted from one test rather than two so the engines cannot silently converge on
    /// the same answer, which is the failure mode a per-engine claim has.
    /// </summary>
    [Fact]
    public async Task Zerofill_IsReportedOnMySqlOnly()
    {
        var result = await BuildAsync("CREATE TABLE dep_zf_warn (c INT ZEROFILL);");

        var deprecations = result.Warnings
            .Where(w => w.Code == SqlSourceDiagnostic.DeprecatedConstruct)
            .ToList();

        if (Fixture.SchemaProviderOf().IsMySql)
        {
            var warning = Assert.Single(deprecations);
            Assert.Contains("ZEROFILL", warning.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("dev.mysql.com", warning.Message);
        }
        else
        {
            Assert.Empty(deprecations);
        }
    }
}

public sealed class MariaDbDeprecatedFeatureTestsMariaDb(MariaDbFixture fixture)
    : MariaDbDeprecatedFeatureTests, IClassFixture<MariaDbFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

public sealed class MariaDbDeprecatedFeatureTestsMySql(MySqlFixture fixture)
    : MariaDbDeprecatedFeatureTests, IClassFixture<MySqlFixture>
{
    protected override MariaDbLikeFixture Fixture { get; } = fixture;
}

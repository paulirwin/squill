using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;
using Squill.TestFramework;

namespace Squill.IntegrationTests.Postgres.DeprecatedFeatureTest;

/// <summary>
/// Verifies the build-time deprecation warning added for issue #190 against a real server.
///
/// <para>
/// What has to be proved here is the inverse of what a target-version test proves. SQ1003 is
/// confirmed by the server <em>rejecting</em> the DDL; SQ1006 says the construct is still accepted
/// and merely advised against, so the assertion is that the warned-about source deploys and
/// round-trips cleanly. If the server refused it the diagnostic would be miscoded, and if the
/// round-trip re-diffed the warning would be dressing up a modeling bug as a style note.
/// </para>
///
/// <para>
/// The type itself already has round-trip coverage in the data-type suite. What is new is that a
/// warning is now attached to it, and the pairing is the point: a build that reports something is
/// deprecated must still produce a deployable, stable model of it.
/// </para>
/// </summary>
public class PostgresDeprecatedFeatureTest : PostgresIntegrationTestBase
{
    private const string TimeWithTimeZoneSql = """
        CREATE TABLE dep_shift
        (
            id         integer PRIMARY KEY,
            starts_at  time with time zone
        );
        """;

    private static async Task<BuildResult> BuildAsync(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Shift.sql", FileKind.Compile, sql));

        return await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The build warns, and the very same source deploys into a real database and re-extracts to
    /// an identical model — deprecated, not removed.
    /// </summary>
    [Fact]
    public async Task TimeWithTimeZone_WarnsAtBuildAndStillDeploys()
    {
        var result = await BuildAsync(TimeWithTimeZoneSql);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(SqlSourceDiagnostic.DeprecatedConstruct, warning.Code);
        Assert.Contains("time with time zone", warning.Message);

        // ...and the server the warning was raised against accepts it regardless.
        await RoundTripHarness.AssertRoundTripAsync(
            new PostgresDatabaseProvider(ConnectionString),
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            TimeWithTimeZoneSql,
            "postgres",
            assertRedeployNoOp: true,
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// <c>timetz</c> is the same type spelled shorter, and the server settles it: a column
    /// declared either way is reported by <c>information_schema</c> as <c>time with time zone</c>,
    /// measured on postgres:latest. That is why the checker treats the two alike — the
    /// abbreviation must not be a way around the warning.
    ///
    /// <para>
    /// The round trip is asserted here too, now that issue #197 resolves the alias to the same
    /// built-in as the spelled-out form. Before that fix the parser modeled the alias as written
    /// while extraction reported the canonical name, so the two models never hash-matched and the
    /// column re-diffed on every deploy.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Timetz_WarnsLikeTheSpelledOutForm()
    {
        const string sql = """
            CREATE TABLE dep_shift_tz
            (
                id         integer PRIMARY KEY,
                starts_at  timetz
            );
            """;

        var result = await BuildAsync(sql);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(SqlSourceDiagnostic.DeprecatedConstruct, warning.Code);
        Assert.Contains("dep_shift_tz.starts_at", warning.Message);

        await RoundTripHarness.AssertRoundTripAsync(
            new PostgresDatabaseProvider(ConnectionString),
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            sql,
            "postgres",
            assertRedeployNoOp: true,
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The recommended alternative must not itself warn, and it deploys as it always did.
    /// </summary>
    [Fact]
    public async Task TimestampWithTimeZone_DoesNotWarn()
    {
        const string sql = """
            CREATE TABLE dep_event
            (
                id           integer PRIMARY KEY,
                occurred_at  timestamp with time zone
            );
            """;

        var result = await BuildAsync(sql);

        Assert.Empty(result.Warnings);
    }
}

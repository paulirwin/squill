using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Tests build-time deprecation reporting (issue #190): source that uses a construct PostgreSQL
/// still accepts but documents as not recommended is reported as an SQ1006 warning.
///
/// The code is distinct from SQ1003 on purpose, and these tests pin that distinction down. SQ1003
/// says the declared target is too old and is fixed by raising it; a deprecation is accepted by
/// every supported major, so no version is at fault and "requires version N or later" would send
/// the author after an upgrade that resolves nothing. The version-independence is asserted
/// directly, across the oldest and newest supported targets.
/// </summary>
public class DeprecatedFeatureTests
{
    private static ParserWorkspaceModelBuilder BuilderFor(
        PostgresqlDatabaseSchemaProvider schemaProvider, params (string Name, string Sql)[] files)
    {
        var workspace = new Workspace();
        foreach (var (name, sql) in files)
        {
            workspace.Files.Add(new InMemoryStringFile(name, FileKind.Compile, sql));
        }

        return new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser(), schemaProvider);
    }

    private const string TimeWithTimeZoneSql = """
CREATE TABLE shift
(
    id integer PRIMARY KEY,
    starts_at time with time zone
);
""";

    [Fact]
    public async Task TimeWithTimeZone_Warns()
    {
        var builder = BuilderFor(
            new Postgresql16DatabaseSchemaProvider(), ("Shift.sql", TimeWithTimeZoneSql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);

        Assert.Equal(SqlSourceDiagnostic.DeprecatedConstruct, warning.Code);
        Assert.Equal("Shift.sql", warning.SourceFile);
        Assert.Contains("shift.starts_at", warning.Message);
        Assert.Contains("time with time zone", warning.Message);
    }

    /// <summary>
    /// The heart of why this is not SQ1003. A deprecation is a property of the construct, not of
    /// the target, so the same source reports identically on the oldest and newest supported
    /// majors — and the message must never claim a version is required, which would be both false
    /// and unactionable.
    /// </summary>
    [Fact]
    public async Task TimeWithTimeZone_ReportsIdenticallyOnEverySupportedTarget()
    {
        PostgresqlDatabaseSchemaProvider[] targets =
        [
            new Postgresql14DatabaseSchemaProvider(),
            new Postgresql15DatabaseSchemaProvider(),
            new Postgresql16DatabaseSchemaProvider(),
        ];

        foreach (var target in targets)
        {
            var result = await BuilderFor(target, ("Shift.sql", TimeWithTimeZoneSql))
                .ExtractModelAsync(TestContext.Current.CancellationToken);

            var warning = Assert.Single(result.Warnings);

            Assert.Equal(SqlSourceDiagnostic.DeprecatedConstruct, warning.Code);
            Assert.DoesNotContain("or later", warning.Message);
            Assert.DoesNotContain("targets PostgreSQL", warning.Message);
        }
    }

    /// <summary>
    /// The message has to carry the alternative. A version warning can leave the remedy implicit
    /// because raising the target is the only move; a deprecation cannot, or the author is left
    /// with a complaint and nothing to do about it.
    /// </summary>
    [Fact]
    public async Task TimeWithTimeZone_NamesTheAlternativeAndCitesTheDocumentation()
    {
        var result = await BuilderFor(
                new Postgresql16DatabaseSchemaProvider(), ("Shift.sql", TimeWithTimeZoneSql))
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);

        Assert.Contains("timestamp with time zone", warning.Message);
        Assert.Contains("postgresql.org", warning.Message);
    }

    /// <summary>
    /// SQ1006 reports two different grounds — a vendor scheduling a construct for removal, and a
    /// vendor saying outright not to use one — and the message must not confuse them. PostgreSQL
    /// supports <c>time with time zone</c> "for compliance with the SQL standard" and never says
    /// it is going away, so claiming a removal here would overstate what the cited page says.
    /// The MySQL-side warnings do claim one, because their pages do.
    /// </summary>
    [Fact]
    public async Task TimeWithTimeZone_DoesNotClaimTheTypeWillBeRemoved()
    {
        var result = await BuilderFor(
                new Postgresql16DatabaseSchemaProvider(), ("Shift.sql", TimeWithTimeZoneSql))
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);

        Assert.Contains("not recommended", warning.Message);
        Assert.DoesNotContain("remove", warning.Message);
    }

    /// <summary>
    /// <c>timetz</c> is the same type by its shorter name — Postgres reports a column declared
    /// that way as <c>time with time zone</c>. It reaches the syntax tree by a different route
    /// (unresolved, rather than a keyword-resolved built-in), so if it were missed the warning
    /// would be avoidable by choosing the abbreviation.
    ///
    /// The schema-qualified spelling <c>pg_catalog.timetz</c> is absent because the parser rejects
    /// a qualified generic type outright, well before the model builder could report on it.
    /// </summary>
    [Theory]
    [InlineData("timetz")]
    [InlineData("TIMETZ")]
    public async Task Timetz_WarnsLikeTheSpelledOutForm(string declared)
    {
        var sql = $"""
CREATE TABLE shift
(
    id integer PRIMARY KEY,
    starts_at {declared}
);
""";
        var result = await BuilderFor(
                new Postgresql16DatabaseSchemaProvider(), ("Shift.sql", sql))
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);

        Assert.Equal(SqlSourceDiagnostic.DeprecatedConstruct, warning.Code);
        Assert.Contains("shift.starts_at", warning.Message);
    }

    /// <summary>
    /// An array of the deprecated type is still a declaration of it, and only the element type
    /// says so.
    /// </summary>
    [Fact]
    public async Task TimeWithTimeZoneArray_Warns()
    {
        const string sql = """
CREATE TABLE shift
(
    id integer PRIMARY KEY,
    breaks time with time zone[]
);
""";
        var result = await BuilderFor(
                new Postgresql16DatabaseSchemaProvider(), ("Shift.sql", sql))
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);

        Assert.Equal(SqlSourceDiagnostic.DeprecatedConstruct, warning.Code);
        Assert.Contains("shift.breaks", warning.Message);
    }

    /// <summary>
    /// <c>time without time zone</c> is not deprecated — the documentation's objection is to the
    /// zone, which without a date cannot resolve an offset. Warning on the plain type would fire
    /// on a large fraction of real schemas for no stated reason.
    /// </summary>
    [Fact]
    public async Task PlainTime_DoesNotWarn()
    {
        const string sql = """
CREATE TABLE shift
(
    id integer PRIMARY KEY,
    starts_at time,
    ends_at time without time zone
);
""";
        var result = await BuilderFor(
                new Postgresql16DatabaseSchemaProvider(), ("Shift.sql", sql))
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// <c>timestamp with time zone</c> is the type the warning recommends, so warning on it would
    /// be a contradiction — and the two spellings are one token apart.
    /// </summary>
    [Fact]
    public async Task TimestampWithTimeZone_DoesNotWarn()
    {
        const string sql = """
CREATE TABLE event
(
    id integer PRIMARY KEY,
    occurred_at timestamp with time zone,
    also_at timestamptz
);
""";
        var result = await BuilderFor(
                new Postgresql16DatabaseSchemaProvider(), ("Event.sql", sql))
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// The candidates issue #190 investigated and disproved. Each was checked against its own
    /// documentation page and found to carry advice, a caveat, or an alternative — but no
    /// deprecation. They are asserted silent so that a later change cannot quietly start warning
    /// on them: an unfounded deprecation claim is worse than a missing one, because it teaches
    /// authors to suppress the code wholesale.
    /// </summary>
    [Theory]
    [InlineData("price money", "money has a locale caveat, not a deprecation")]
    [InlineData("id serial", "serial is an alternative to identity columns, not a superseded form")]
    [InlineData("id bigserial", "bigserial likewise")]
    [InlineData("code character(10)", "character(n) is advised against only as a usual preference")]
    [InlineData("code char(10)", "the char spelling likewise")]
    public async Task DisprovedCandidates_DoNotWarn(string column, string why)
    {
        var sql = $"""
CREATE TABLE thing
(
    {column}
);
""";
        var result = await BuilderFor(
                new Postgresql16DatabaseSchemaProvider(), ("Thing.sql", sql))
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.True(
            result.Warnings.All(w => w.Code != SqlSourceDiagnostic.DeprecatedConstruct),
            $"Expected no SQ1006 for '{column}': {why}.");
    }

    [Fact]
    public async Task OrdinarySource_DoesNotWarn()
    {
        const string sql = """
CREATE TABLE account
(
    id integer PRIMARY KEY,
    email text
);
""";
        var result = await BuilderFor(
                new Postgresql16DatabaseSchemaProvider(), ("Account.sql", sql))
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }
}

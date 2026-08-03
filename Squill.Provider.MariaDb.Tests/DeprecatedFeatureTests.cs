using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Tests build-time deprecation reporting (issue #190): source that uses a construct the target
/// engine still accepts but documents as scheduled for removal is reported as an SQ1006 warning.
///
/// Two things these tests exist to pin down. First, SQ1006 is not SQ1003: a deprecated construct
/// is accepted by every version in the supported window, so no target version is at fault and
/// raising one would resolve nothing — the version-independence is asserted directly across
/// majors. Second, deprecation here is <em>per engine</em>. Every construct covered is deprecated
/// by MySQL and documented by MariaDB as ordinary current functionality, so the same source must
/// warn on one engine and stay silent on the other. Warning a MariaDB project about MySQL's
/// removal plans would be a claim MariaDB's documentation does not support.
/// </summary>
public class DeprecatedFeatureTests
{
    private static ParserWorkspaceModelBuilder BuilderFor(
        MariaDbFamilyDatabaseSchemaProvider engine, params (string Name, string Sql)[] files)
    {
        var workspace = new Workspace();
        foreach (var (name, sql) in files)
        {
            workspace.Files.Add(new InMemoryStringFile(name, FileKind.Compile, sql));
        }

        return new ParserWorkspaceModelBuilder(workspace, new AntlrMariaDbParser(), engine);
    }

    private static async Task<IReadOnlyList<SqlSourceDiagnostic>> DeprecationsFor(
        MariaDbFamilyDatabaseSchemaProvider engine, string columns)
    {
        var sql = $"CREATE TABLE t ({columns});";

        var result = await BuilderFor(engine, ("T.sql", sql))
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        return result.Warnings
            .Where(w => w.Code == SqlSourceDiagnostic.DeprecatedConstruct)
            .ToList();
    }

    /// <summary>
    /// The four MySQL 8.0.17 numeric-attribute deprecations plus the utf8 alias, each reported on
    /// MySQL. The quoted sentences behind each are recorded on
    /// <see cref="MariaDbDeprecatedFeature"/>.
    /// </summary>
    [Theory]
    [InlineData("c int zerofill", "ZEROFILL")]
    [InlineData("c int(11)", "display width")]
    [InlineData("c double unsigned", "UNSIGNED")]
    [InlineData("c decimal(10,2) unsigned", "UNSIGNED")]
    [InlineData("c double auto_increment primary key", "AUTO_INCREMENT")]
    [InlineData("c varchar(10) character set utf8", "utf8")]
    public async Task DeprecatedConstruct_OnMySql_Warns(string column, string expected)
    {
        var warnings = await DeprecationsFor(new MySql8DatabaseSchemaProvider(), column);

        var warning = Assert.Single(warnings);

        Assert.Equal("T.sql", warning.SourceFile);
        Assert.Contains(expected, warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("t.c", warning.Message);
    }

    /// <summary>
    /// The same source on MariaDB, which deprecates none of these. Measured against the MariaDB
    /// Knowledge Base rather than assumed from MySQL: it documents ZEROFILL, display widths and
    /// the utf8 alias as current functionality with no removal language, and its utf8 alias is
    /// configurable (old_mode) rather than scheduled for removal.
    /// </summary>
    [Theory]
    [InlineData("c int zerofill")]
    [InlineData("c int(11)")]
    [InlineData("c double unsigned")]
    [InlineData("c decimal(10,2) unsigned")]
    [InlineData("c double auto_increment primary key")]
    [InlineData("c varchar(10) character set utf8")]
    public async Task DeprecatedByMySqlOnly_OnMariaDb_DoesNotWarn(string column)
    {
        var warnings = await DeprecationsFor(new MariaDb11DatabaseSchemaProvider(), column);

        Assert.Empty(warnings);
    }

    /// <summary>
    /// Why this is not SQ1003: the report is identical on every supported major, and the message
    /// must never claim a version is required. On MySQL 8 the construct is deprecated and on
    /// MySQL 9 it still is; there is no version to upgrade to that resolves it.
    /// </summary>
    [Fact]
    public async Task Deprecation_ReportsIdenticallyOnEverySupportedMySqlMajor()
    {
        MariaDbFamilyDatabaseSchemaProvider[] targets =
            [new MySql8DatabaseSchemaProvider(), new MySql9DatabaseSchemaProvider()];

        foreach (var target in targets)
        {
            var warning = Assert.Single(await DeprecationsFor(target, "c int zerofill"));

            Assert.Equal(SqlSourceDiagnostic.DeprecatedConstruct, warning.Code);
            Assert.DoesNotContain("or later", warning.Message);
            Assert.Contains("deprecates", warning.Message);
        }
    }

    /// <summary>
    /// A deprecation warning that only says "stop doing this" leaves the author with nothing to
    /// do, since no version bump will help. The alternative and the citation are both required.
    /// </summary>
    [Fact]
    public async Task Deprecation_NamesTheAlternativeAndCitesTheDocumentation()
    {
        var warning = Assert.Single(
            await DeprecationsFor(new MySql8DatabaseSchemaProvider(), "c int(11)"));

        Assert.Contains("dev.mysql.com", warning.Message);
        Assert.Contains("drop the width", warning.Message);
    }

    /// <summary>
    /// The converse of the Postgres side's assertion. SQ1006 reports both scheduled removal and
    /// mere non-recommendation, and every construct here is the former — each cites a MySQL page
    /// saying to expect support to be removed — so unlike the Postgres warnings, these say so.
    /// </summary>
    [Fact]
    public async Task Deprecation_StatesThatSupportWillBeRemoved()
    {
        var warning = Assert.Single(
            await DeprecationsFor(new MySql8DatabaseSchemaProvider(), "c int zerofill"));

        Assert.Contains("remove", warning.Message);
    }

    /// <summary>
    /// MySQL 8.4 rejects AUTO_INCREMENT on a floating-point column outright rather than merely
    /// deprecating it. Squill targets a major, which cannot tell 8.0 from 8.4, so the note is how
    /// an author learns the construct may already be fatal on their server.
    /// </summary>
    [Fact]
    public async Task FloatAutoIncrement_NotesThatMySql84RejectsIt()
    {
        var warning = Assert.Single(await DeprecationsFor(
            new MySql8DatabaseSchemaProvider(), "c float auto_increment primary key"));

        Assert.Contains("8.4", warning.Message);
    }

    /// <summary>
    /// A column can be deprecated on more than one count, and each has to be fixed separately, so
    /// each is reported separately rather than collapsed into one finding.
    /// </summary>
    [Fact]
    public async Task ColumnWithSeveralDeprecations_ReportsEachOne()
    {
        var warnings = await DeprecationsFor(
            new MySql8DatabaseSchemaProvider(), "c double unsigned zerofill auto_increment primary key");

        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains("ZEROFILL"));
        Assert.Contains(warnings, w => w.Message.Contains("UNSIGNED"));
        Assert.Contains(warnings, w => w.Message.Contains("AUTO_INCREMENT"));
    }

    /// <summary>
    /// The boundaries that keep SQ1006 from firing on ordinary schemas. UNSIGNED on an integer is
    /// not deprecated and is the most common attribute in MySQL schemas; a length on a non-integer
    /// type is a length, not a display width, even though both reach the syntax tree as a
    /// modifier; and AUTO_INCREMENT is only deprecated on floating-point columns, which is what it
    /// is almost never on.
    /// </summary>
    [Theory]
    [InlineData("c int unsigned", "UNSIGNED on an integer is not deprecated")]
    [InlineData("c bigint unsigned primary key", "nor on a bigint")]
    [InlineData("c varchar(255)", "a varchar length is not a display width")]
    [InlineData("c decimal(10,2)", "nor is a decimal precision")]
    [InlineData("c datetime(3)", "nor a fractional-seconds precision")]
    [InlineData("c year(4)", "nor a year width")]
    [InlineData("c bit(8)", "nor a bit length")]
    [InlineData("c int auto_increment primary key", "AUTO_INCREMENT on an integer is the normal case")]
    [InlineData("c varchar(10) character set utf8mb4", "utf8mb4 is the recommended set")]
    [InlineData("c varchar(10) character set utf8mb3", "utf8mb3 spelled out is not the utf8 alias")]
    public async Task NonDeprecatedConstruct_OnMySql_DoesNotWarn(string column, string why)
    {
        var warnings = await DeprecationsFor(new MySql8DatabaseSchemaProvider(), column);

        Assert.True(warnings.Count == 0, $"Expected no SQ1006 for '{column}': {why}.");
    }

    /// <summary>
    /// The alias is a spelling, and the deprecated spelling is what is reported — so the check is
    /// case-insensitive, since UTF8 declares exactly the same character set as utf8.
    /// </summary>
    [Theory]
    [InlineData("utf8")]
    [InlineData("UTF8")]
    public async Task Utf8Alias_IsMatchedRegardlessOfCase(string charset)
    {
        var warnings = await DeprecationsFor(
            new MySql8DatabaseSchemaProvider(), $"c varchar(10) character set {charset}");

        Assert.Single(warnings);
    }

    [Fact]
    public async Task OrdinarySource_DoesNotWarn()
    {
        var warnings = await DeprecationsFor(
            new MySql8DatabaseSchemaProvider(),
            "id int auto_increment primary key, email varchar(255) not null");

        Assert.Empty(warnings);
    }
}

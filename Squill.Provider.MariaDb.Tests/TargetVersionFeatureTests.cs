using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Tests build-time target-version feature validation (issue #142): source that uses a
/// construct introduced in a newer engine version than the project's declared
/// <c>SquillTargetVersion</c> is reported as an SQ1003 warning, so it is caught at build rather
/// than as a syntax error partway through a deploy.
///
/// The two engines this provider serves diverge sharply on the constructs involved, which is
/// why the feature's minimum version is asked of the schema provider rather than being a
/// constant: <c>VECTOR</c> arrived in MySQL 9 and in MariaDB 11, so the same source is fine on
/// one engine's major and too new on the other's.
/// </summary>
public class TargetVersionFeatureTests
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

    private const string VectorSql = """
CREATE TABLE embedding
(
    id BIGINT UNSIGNED NOT NULL PRIMARY KEY,
    v VECTOR(1536) NOT NULL
);
""";

    private const string UuidSql = """
CREATE TABLE account
(
    id UUID NOT NULL PRIMARY KEY
);
""";

    [Fact]
    public async Task Vector_OnMariaDb10_Warns()
    {
        // VECTOR arrived in MariaDB 11.7, so a project targeting MariaDB 10 must hear about it.
        var builder = BuilderFor(new MariaDb10DatabaseSchemaProvider(), ("Embedding.sql", VectorSql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(SqlSourceDiagnostic.FeatureNotInTargetVersion, warning.Code);
        Assert.Equal("Embedding.sql", warning.SourceFile);
        Assert.Contains("VECTOR", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("11", warning.Message);
    }

    [Fact]
    public async Task Vector_OnMariaDb11_DoesNotWarn()
    {
        var builder = BuilderFor(new MariaDb11DatabaseSchemaProvider(), ("Embedding.sql", VectorSql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Vector_OnMySql8_Warns()
    {
        // The same construct, a different engine and a different boundary: VECTOR is MySQL 9,
        // so 8 is too old. The minimum version has to come from the schema provider — a single
        // hardcoded number would be wrong for one engine or the other.
        var builder = BuilderFor(new MySql8DatabaseSchemaProvider(), ("Embedding.sql", VectorSql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(SqlSourceDiagnostic.FeatureNotInTargetVersion, warning.Code);
        Assert.Contains("VECTOR", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("9", warning.Message);
    }

    [Fact]
    public async Task Vector_OnMySql9_DoesNotWarn()
    {
        var builder = BuilderFor(new MySql9DatabaseSchemaProvider(), ("Embedding.sql", VectorSql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task Uuid_OnMariaDb10_Warns()
    {
        // UUID is a MariaDB 10.7 type. Squill's target is a major only, so 10 cannot be
        // distinguished from 10.7 — see MariaDbVersionedFeature.Uuid for why this is still
        // reported against MariaDB 10 rather than left silent.
        var builder = BuilderFor(new MariaDb10DatabaseSchemaProvider(), ("Account.sql", UuidSql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(SqlSourceDiagnostic.FeatureNotInTargetVersion, warning.Code);
        Assert.Contains("UUID", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Uuid_OnMySql_WarnsAsUnsupportedRatherThanTooNew()
    {
        // MySQL has no UUID type at any version, so "too new" would be a lie — there is no
        // version to upgrade to. It is reported as unsupported-on-this-engine instead.
        var builder = BuilderFor(new MySql9DatabaseSchemaProvider(), ("Account.sql", UuidSql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(SqlSourceDiagnostic.FeatureNotSupportedByEngine, warning.Code);
        Assert.Contains("UUID", warning.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySql", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Uuid_OnMariaDb11_DoesNotWarn()
    {
        var builder = BuilderFor(new MariaDb11DatabaseSchemaProvider(), ("Account.sql", UuidSql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task OrdinarySource_OnOldestTarget_DoesNotWarn()
    {
        const string sql = """
CREATE TABLE account
(
    id INT NOT NULL PRIMARY KEY,
    email VARCHAR(255)
);
""";
        var builder = BuilderFor(new MariaDb10DatabaseSchemaProvider(), ("Account.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }
}

using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Tests build-time target-version feature validation (issue #142): source that uses a
/// construct newer than the project's declared <c>SquillTargetVersion</c> is reported as an
/// SQ1003 warning, so the gap is caught at build rather than as a syntax error on the server
/// mid-deploy.
///
/// A warning rather than an error because that is what the issue asks for, and because the
/// diagnostic rides the same channel as every other <c>SQ1xxx</c> — a project that wants it
/// fatal escalates it with <c>MSBuildWarningsAsErrors</c>, like any other coded warning. (Not
/// the <c>WarningsAsErrors</c> a C# project would use — that is a Roslyn compiler option and
/// does not apply to a warning logged by an MSBuild task, which is what this one is.)
/// </summary>
public class TargetVersionFeatureTests
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

    // NULLS NOT DISTINCT arrived in PostgreSQL 15; on 14 it is a syntax error, so a project
    // targeting 14 must hear about it at build time.
    private const string NullsNotDistinctSql = """
CREATE TABLE account
(
    id integer PRIMARY KEY,
    email text
);

CREATE UNIQUE INDEX ix_account_email ON account (email) NULLS NOT DISTINCT;
""";

    [Fact]
    public async Task NullsNotDistinct_OnOlderTarget_Warns()
    {
        var builder = BuilderFor(
            new Postgresql14DatabaseSchemaProvider(), ("Account.sql", NullsNotDistinctSql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal(SqlSourceDiagnostic.FeatureNotInTargetVersion, warning.Code);
        Assert.Equal("Account.sql", warning.SourceFile);
        Assert.Equal(7, warning.Line);
        Assert.Contains("NULLS NOT DISTINCT", warning.Message);

        // The version that introduced it and the version being targeted are both named: the
        // fix is either to raise the target or drop the construct, and the message has to say
        // enough to choose between them.
        Assert.Contains("15", warning.Message);
        Assert.Contains("14", warning.Message);
    }

    [Fact]
    public async Task NullsNotDistinct_OnIntroducingTarget_DoesNotWarn()
    {
        // 15 is the exact version that introduced it: the comparison is >=, not >, so the
        // introducing version must be silent.
        var builder = BuilderFor(
            new Postgresql15DatabaseSchemaProvider(), ("Account.sql", NullsNotDistinctSql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task NullsNotDistinct_OnNewerTarget_DoesNotWarn()
    {
        var builder = BuilderFor(
            new Postgresql18DatabaseSchemaProvider(), ("Account.sql", NullsNotDistinctSql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task NullsNotDistinct_StillModeledOnOlderTarget()
    {
        // The warning does not change what is built. Dropping the construct would deploy an
        // index with the opposite uniqueness semantics from the one the source declares, which
        // is worse than deploying nothing — so the model keeps it and the warning is the whole
        // of the response.
        var builder = BuilderFor(
            new Postgresql14DatabaseSchemaProvider(), ("Account.sql", NullsNotDistinctSql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var index = Assert.Single(
            result.Model.Elements, e => e.Type == PostgresElementTypes.SqlIndex);

        Assert.Contains(index.Properties,
            p => p.Name == PostgresPropertyNames.NullsNotDistinct && Equals(p.Value, true));
    }

    [Fact]
    public async Task SourceWithoutNewFeatures_DoesNotWarn()
    {
        // The oldest supported target with entirely ordinary source: nothing to report.
        const string sql = """
CREATE TABLE account
(
    id integer PRIMARY KEY,
    email text
);

CREATE UNIQUE INDEX ix_account_email ON account (email);
""";
        var builder = BuilderFor(new Postgresql14DatabaseSchemaProvider(), ("Account.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }
}

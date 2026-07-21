using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Tests the non-fatal diagnostics channel (issue #61): constructs that are declared in the
/// source but not carried into the model do not fail the build, but are reported as SQ1002
/// warnings so the gap is visible rather than silent.
/// </summary>
public class BuildWarningTests
{
    private static ParserWorkspaceModelBuilder BuilderFor(params (string Name, string Sql)[] files)
    {
        var workspace = new Workspace();
        foreach (var (name, sql) in files)
        {
            workspace.Files.Add(new InMemoryStringFile(name, FileKind.Compile, sql));
        }

        return new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser());
    }

    [Fact]
    public async Task FunctionDefault_WarnsAndStillBuilds()
    {
        const string sql = """
CREATE TABLE event
(
    id integer PRIMARY KEY,
    created_at timestamp DEFAULT now()
);
""";
        var builder = BuilderFor(("Event.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        // The build succeeds — a dropped default is not fatal.
        Assert.Contains(result.Model.Elements, e => e.Type == PostgresElementTypes.SqlTable);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Equal("Event.sql", warning.SourceFile);
        Assert.Equal(4, warning.Line);
        Assert.Contains("created_at", warning.Message);
    }

    [Fact]
    public async Task MultipleFunctionDefaults_EachWarn()
    {
        const string sql = """
CREATE TABLE event
(
    id integer PRIMARY KEY,
    created_at timestamp DEFAULT now(),
    updated_at timestamp DEFAULT now()
);
""";
        var builder = BuilderFor(("Event.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Warnings.Count);
        Assert.All(result.Warnings, w => Assert.Equal("SQ1002", w.Code));
        Assert.Contains(result.Warnings, w => w.Message.Contains("created_at"));
        Assert.Contains(result.Warnings, w => w.Message.Contains("updated_at"));
    }

    [Fact]
    public async Task ConstantDefault_DoesNotWarn()
    {
        const string sql = """
CREATE TABLE event
(
    id integer PRIMARY KEY,
    count integer DEFAULT 0
);
""";
        var builder = BuilderFor(("Event.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task CheckConstraint_Warns()
    {
        const string sql = """
CREATE TABLE product
(
    id integer PRIMARY KEY,
    price integer,
    CONSTRAINT ck_price CHECK (price > 0)
);
""";
        var builder = BuilderFor(("Product.sql", sql));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(result.Warnings);
        Assert.Equal("SQ1002", warning.Code);
        Assert.Contains("CHECK", warning.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CleanSource_ProducesNoWarnings()
    {
        var builder = BuilderFor(
            ("Author.sql", "CREATE TABLE author (id integer PRIMARY KEY, name varchar(50) NOT NULL);"),
            ("Book.sql", "CREATE TABLE book (id integer PRIMARY KEY, author_id integer REFERENCES author (id));"));

        var result = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result.Warnings);
    }
}

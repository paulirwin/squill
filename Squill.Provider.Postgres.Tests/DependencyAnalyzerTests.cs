using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

public class DependencyAnalyzerTests
{
    [Fact]
    public async Task GetDependentElements_IndexIsDependentOnItsTable()
    {
        const string sql = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title varchar(255) NOT NULL
);

CREATE INDEX idx_title ON film (title);
""";

        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Film.sql", FileKind.Compile, sql));

        var builder = new ParserWorkspaceModelBuilder(workspace, parser);
        var model = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var analyzer = new PostgresDatabaseDependencyAnalyzer();

        Assert.True(analyzer.IsDependentElementType(PostgresElementTypes.SqlIndex));

        var table = model.Elements.Single(i => i.Type == PostgresElementTypes.SqlTable);
        var dependents = analyzer.GetDependentElements(table, model);

        Assert.NotNull(dependents);
        var index = Assert.Single(dependents, i => i.Type == PostgresElementTypes.SqlIndex);
        Assert.Equal("\"idx_title\"", index.Name);
    }
}

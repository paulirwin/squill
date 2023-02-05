using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

public class WorkspaceModelBuilderTests
{
    [Fact]
    public async Task ExtractModel_GivenEmptyWorkspace_ShouldReturnEmptyModel()
    {
        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();

        var builder = new ParserWorkspaceModelBuilder(workspace, parser);

        var model = await builder.ExtractModelAsync();
        
        Assert.NotNull(model);
        Assert.Equal(0, model.Elements.Count);
    }
}
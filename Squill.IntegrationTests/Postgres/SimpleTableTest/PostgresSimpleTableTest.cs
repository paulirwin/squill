using Squill.Core;
using Squill.Core.Postgres;

namespace Squill.IntegrationTests.Postgres.SimpleTableTest;

public class PostgresSimpleTableTest
{
    [Fact]
    public async Task SimpleCreateTableTest()
    {
        var provider = new PostgresDatabaseProvider("Host=localhost;");

        var modelBuilder = new ModelBuilder(provider);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile("Squill.IntegrationTests.Postgres.SimpleTableTest.SimpleTable.sql", FileKind.Compile));
        
        var model = await modelBuilder.BuildModelAsync(workspace);
        
        Assert.Equal(1, model.Elements.Count);
        Assert.Equal("SqlTable", model.Elements[0].Type);
        Assert.Equal("distributors", model.Elements[0].Name);

        var columns = model.Elements[0].Relationships.First(i => i.Name == PostgresRelationshipNames.Columns);
        
        Assert.Equal(2, columns.Entries.Count); 
    }
}
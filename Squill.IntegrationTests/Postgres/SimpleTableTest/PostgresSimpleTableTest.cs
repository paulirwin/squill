using Squill.Core;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.SimpleTableTest;

public class PostgresSimpleTableTest
{
    [Fact]
    public async Task SimpleCreateTableTest()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider("Host=localhost;");

        var modelBuilder = new ModelBuilder(provider);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile("Squill.IntegrationTests.Postgres.SimpleTableTest.SimpleTable.sql", FileKind.Compile));
        
        var model = await modelBuilder.BuildModelAsync(workspace);

        var tables = model.Elements.Where(i => i.Type.Equals(PostgresElementTypes.SqlTable)).ToList();
        
        Assert.Single(tables);
        Assert.Equal("distributors", tables[0].Name);

        var columns = tables[0].Relationships.First(i => i.Name == PostgresRelationshipNames.Columns);
        
        Assert.Equal(2, columns.Entries.Count);

        var testDb = await provider.CreateDatabaseAsync($"squill_test_{Guid.NewGuid():n}");

        try
        {
            var testModel = await testDb.ExtractModelAsync();

            // HACK.PI: this API feels a little sloppy, but we'll get there
            var comparison = SchemaCompare.Compare(provider, model, testModel);

            await testDb.PublishAsync(comparison);

            var newModel = await testDb.ExtractModelAsync();
            
            Assert.True(HashUtility.HashesEqual(model.Hash, newModel.Hash), "Model hashes do not match");
        }
        finally
        {
            await testDb.DropAsync();
        }
    }
}
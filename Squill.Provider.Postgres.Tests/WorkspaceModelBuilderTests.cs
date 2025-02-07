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
    
    [Fact]
    public async Task ExtractModel_GivenNonCompiledFile_ShouldReturnEmptyModel()
    {
        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Ignored.sql", FileKind.None, "CREATE TABLE Whatever;"));

        var builder = new ParserWorkspaceModelBuilder(workspace, parser);

        var model = await builder.ExtractModelAsync();
        
        Assert.NotNull(model);
        Assert.Equal(0, model.Elements.Count);
    }

    [Fact]
    public async Task ExtractModel_SimpleCreateTableTest()
    {
        const string sql = """
CREATE TABLE Foo 
(
    id integer PRIMARY KEY,
    name varchar(100) NOT NULL
);
""";
        
        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Foo.sql", FileKind.Compile, sql));

        var builder = new ParserWorkspaceModelBuilder(workspace, parser);

        var model = await builder.ExtractModelAsync();
        
        Assert.NotNull(model);
        Assert.Equal(1, model.Elements.Count);

        var table = model.Elements[0];
        
        Assert.Equal("\"Foo\"", table.Name);
        Assert.Equal(PostgresElementTypes.SqlTable, table.Type);
        
        Assert.Equal(1, table.Relationships.Count);
        var columns = table.Relationships[0];
        Assert.Equal(PostgresRelationshipNames.Columns, columns.Name);
        Assert.Equal(2, columns.Entries.Count);
        
        var idCol = Assert.IsType<Element>(columns.Entries[0]);
        Assert.Equal(PostgresElementTypes.SqlSimpleColumn, idCol.Type);
        Assert.Equal("\"Foo\".\"id\"", idCol.Name);
        Assert.Equal(1, idCol.Properties.Count);
        Assert.Equal(PostgresPropertyNames.IsNullable, idCol.Properties[0].Name);
        Assert.Equal(false, idCol.Properties[0].Value);
        Assert.Equal(1, idCol.Relationships.Count);
        Assert.Equal(PostgresRelationshipNames.TypeSpecifier, idCol.Relationships[0].Name);
        Assert.Equal(1, idCol.Relationships[0].Entries.Count);
        var idTypeElem = Assert.IsType<Element>(idCol.Relationships[0].Entries[0]);
        Assert.Equal(PostgresElementTypes.SqlTypeSpecifier, idTypeElem.Type);
        Assert.Equal(1, idTypeElem.Relationships.Count);
        Assert.Equal(PostgresRelationshipNames.Type, idTypeElem.Relationships[0].Name);
        Assert.Equal(1, idTypeElem.Relationships[0].Entries.Count);
        var idTypeRef = Assert.IsType<Reference>(idTypeElem.Relationships[0].Entries[0]);
        Assert.Equal("BuiltIns", idTypeRef.ExternalSource);
        Assert.Equal("integer", idTypeRef.Name);
    }
}
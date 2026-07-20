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

        var model = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(model);
        Assert.Empty(model.Elements);
    }

    [Fact]
    public async Task ExtractModel_GivenNonCompiledFile_ShouldReturnEmptyModel()
    {
        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Ignored.sql", FileKind.None, "CREATE TABLE Whatever;"));

        var builder = new ParserWorkspaceModelBuilder(workspace, parser);

        var model = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(model);
        Assert.Empty(model.Elements);
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

        var model = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(model);
        // A table with an inline PRIMARY KEY now yields two elements: the table and
        // a standalone primary-key constraint element (matching the DB builder).
        Assert.Equal(2, model.Elements.Count);

        var table = model.Elements.Single(i => i.Type == PostgresElementTypes.SqlTable);

        Assert.Equal("Foo", table.Name);
        Assert.Equal(PostgresElementTypes.SqlTable, table.Type);

        Assert.Single(table.Relationships);
        var columns = table.Relationships[0];
        Assert.Equal(PostgresRelationshipNames.Columns, columns.Name);
        Assert.Equal(2, columns.Entries.Count);

        var idCol = Assert.IsType<Element>(columns.Entries[0]);
        Assert.Equal(PostgresElementTypes.SqlSimpleColumn, idCol.Type);
        Assert.Equal("Foo.id", idCol.Name);
        Assert.Single(idCol.Properties);
        Assert.Equal(PostgresPropertyNames.IsNullable, idCol.Properties[0].Name);
        Assert.Equal(false, idCol.Properties[0].Value);
        Assert.Single(idCol.Relationships);
        Assert.Equal(PostgresRelationshipNames.TypeSpecifier, idCol.Relationships[0].Name);
        Assert.Single(idCol.Relationships[0].Entries);
        var idTypeElem = Assert.IsType<Element>(idCol.Relationships[0].Entries[0]);
        Assert.Equal(PostgresElementTypes.SqlTypeSpecifier, idTypeElem.Type);
        Assert.Single(idTypeElem.Relationships);
        Assert.Equal(PostgresRelationshipNames.Type, idTypeElem.Relationships[0].Name);
        Assert.Single(idTypeElem.Relationships[0].Entries);
        var idTypeRef = Assert.IsType<Reference>(idTypeElem.Relationships[0].Entries[0]);
        Assert.Equal("BuiltIns", idTypeRef.ExternalSource);
        Assert.Equal("integer", idTypeRef.Name);
    }

    [Fact]
    public async Task ExtractModel_SimpleCreateIndexTest()
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

        // table + primary-key constraint + index
        Assert.Equal(3, model.Elements.Count);

        var index = model.Elements.Single(i => i.Type == PostgresElementTypes.SqlIndex);
        Assert.Equal("idx_title", index.Name);
        Assert.Equal(false, index.GetProperty<bool?>(PostgresPropertyNames.IsUnique));
        Assert.Null(index.GetProperty<string>(PostgresPropertyNames.IndexMethod));

        var indexedObject = index.GetRelationship(PostgresRelationshipNames.IndexedObject);
        Assert.NotNull(indexedObject);
        var tableRef = Assert.IsType<Reference>(Assert.Single(indexedObject.Entries));
        Assert.Equal("film", tableRef.Name);

        var columnSpecs = index.GetRelationship(PostgresRelationshipNames.ColumnSpecifications);
        Assert.NotNull(columnSpecs);
        var columnSpec = Assert.IsType<Element>(Assert.Single(columnSpecs.Entries));
        Assert.Equal(PostgresElementTypes.SqlIndexedColumnSpecification, columnSpec.Type);
        var columnRel = columnSpec.GetRelationship(PostgresRelationshipNames.Column);
        Assert.NotNull(columnRel);
        var columnRef = Assert.IsType<Reference>(Assert.Single(columnRel.Entries));
        Assert.Equal("film.title", columnRef.Name);
    }

    [Fact]
    public async Task ExtractModel_UniqueIndexWithMethodDirectionAndNullOrderTest()
    {
        const string sql = """
CREATE UNIQUE INDEX idx_email ON users USING btree (email DESC NULLS LAST);
""";

        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("UsersEmailIndex.sql", FileKind.Compile, sql));

        var builder = new ParserWorkspaceModelBuilder(workspace, parser);

        var model = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var index = Assert.Single(model.Elements);
        Assert.Equal(PostgresElementTypes.SqlIndex, index.Type);
        Assert.Equal("idx_email", index.Name);
        Assert.Equal(true, index.GetProperty<bool?>(PostgresPropertyNames.IsUnique));
        Assert.Equal("btree", index.GetProperty<string>(PostgresPropertyNames.IndexMethod));

        var columnSpecs = index.GetRelationship(PostgresRelationshipNames.ColumnSpecifications);
        Assert.NotNull(columnSpecs);
        var columnSpec = Assert.IsType<Element>(Assert.Single(columnSpecs.Entries));
        Assert.Equal(false, columnSpec.GetProperty<bool?>(PostgresPropertyNames.IsAscending));
        Assert.Equal(false, columnSpec.GetProperty<bool?>(PostgresPropertyNames.NullsFirst));
    }

}

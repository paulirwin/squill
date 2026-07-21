using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

public class WorkspaceModelBuilderTests
{
    private static async Task<Model> BuildAsync(string sql)
    {
        var parser = new AntlrMariaDbParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        var builder = new ParserWorkspaceModelBuilder(workspace, parser);

        return (await builder.ExtractModelAsync(TestContext.Current.CancellationToken)).Model;
    }

    [Fact]
    public async Task ExtractModel_GivenEmptyWorkspace_ReturnsEmptyModel()
    {
        var parser = new AntlrMariaDbParser();
        var workspace = new Workspace();

        var builder = new ParserWorkspaceModelBuilder(workspace, parser);

        var model = (await builder.ExtractModelAsync(TestContext.Current.CancellationToken)).Model;

        Assert.Empty(model.Elements);
    }

    [Fact]
    public async Task ExtractModel_GivenNonCompiledFile_ReturnsEmptyModel()
    {
        var parser = new AntlrMariaDbParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Ignored.sql", FileKind.None, "CREATE TABLE foo (id int);"));

        var builder = new ParserWorkspaceModelBuilder(workspace, parser);

        var model = (await builder.ExtractModelAsync(TestContext.Current.CancellationToken)).Model;

        Assert.Empty(model.Elements);
    }

    [Fact]
    public async Task ExtractModel_SimpleCreateTable_YieldsTableAndColumns()
    {
        const string sql = """
            CREATE TABLE foo
            (
                id int NOT NULL,
                name varchar(100) NOT NULL
            );
            """;

        var model = await BuildAsync(sql);

        var table = Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlTable);
        Assert.Equal("foo", table.Name);

        // Unlike Postgres, a MariaDB table carries no schema relationship — just its columns.
        var columns = Assert.Single(table.Relationships, r => r.Name == MariaDbRelationshipNames.Columns);
        Assert.Equal(2, columns.Entries.Count);

        var idColumn = Assert.IsType<Element>(columns.Entries[0]);
        Assert.Equal("foo.id", idColumn.Name);
        Assert.Equal(false, idColumn.GetProperty<bool?>(MariaDbPropertyNames.IsNullable));

        var typeSpecifier = Assert.Single(idColumn.Relationships, r => r.Name == MariaDbRelationshipNames.TypeSpecifier);
        var typeElement = Assert.IsType<Element>(typeSpecifier.Entries[0]);
        var typeRef = Assert.IsType<Reference>(
            typeElement.GetRelationship(MariaDbRelationshipNames.Type)!.Entries[0]);
        Assert.Equal("int", typeRef.Name);
    }

    [Fact]
    public async Task ExtractModel_InlinePrimaryKey_NamesConstraintPrimary()
    {
        const string sql = """
            CREATE TABLE foo
            (
                id int NOT NULL PRIMARY KEY,
                name varchar(50) NULL
            );
            """;

        var model = await BuildAsync(sql);

        var pk = Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlPrimaryKeyConstraint);
        Assert.Equal("PRIMARY", pk.Name);

        var definingTable = Assert.IsType<Reference>(
            pk.GetRelationship(MariaDbRelationshipNames.DefiningTable)!.Entries[0]);
        Assert.Equal("foo", definingTable.Name);
    }

    [Fact]
    public async Task ExtractModel_AutoIncrementColumn_RecordsProperty()
    {
        const string sql = """
            CREATE TABLE foo
            (
                id int NOT NULL AUTO_INCREMENT PRIMARY KEY
            );
            """;

        var model = await BuildAsync(sql);

        var table = Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlTable);
        var columns = table.GetRelationship(MariaDbRelationshipNames.Columns)!;
        var idColumn = Assert.IsType<Element>(columns.Entries[0]);

        Assert.Equal(true, idColumn.GetProperty<bool?>(MariaDbPropertyNames.IsAutoIncrement));
    }

    [Fact]
    public async Task ExtractModel_VarcharDefault_CanonicalizesToSingleQuoted()
    {
        const string sql = """
            CREATE TABLE foo
            (
                status varchar(20) NOT NULL DEFAULT 'active'
            );
            """;

        var model = await BuildAsync(sql);

        var table = Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlTable);
        var column = Assert.IsType<Element>(table.GetRelationship(MariaDbRelationshipNames.Columns)!.Entries[0]);

        Assert.Equal("'active'", column.GetProperty<string>(MariaDbPropertyNames.DefaultValue));
    }

    [Fact]
    public async Task ExtractModel_DecimalColumn_RecordsPrecisionAndScale()
    {
        const string sql = """
            CREATE TABLE foo
            (
                amount decimal(10, 2) NOT NULL
            );
            """;

        var model = await BuildAsync(sql);

        var table = Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlTable);
        var column = Assert.IsType<Element>(table.GetRelationship(MariaDbRelationshipNames.Columns)!.Entries[0]);
        var typeElement = Assert.IsType<Element>(
            column.GetRelationship(MariaDbRelationshipNames.TypeSpecifier)!.Entries[0]);

        Assert.Equal(10L, typeElement.GetProperty<long?>(MariaDbPropertyNames.Precision));
        Assert.Equal(2L, typeElement.GetProperty<long?>(MariaDbPropertyNames.Scale));
    }

    [Fact]
    public async Task ExtractModel_TableLevelForeignKey_PredictsIbfkName()
    {
        const string sql = """
            CREATE TABLE customer
            (
                id int NOT NULL PRIMARY KEY
            );
            CREATE TABLE orders
            (
                id int NOT NULL PRIMARY KEY,
                customer_id int NOT NULL,
                CONSTRAINT fk_customer FOREIGN KEY (customer_id) REFERENCES customer (id) ON DELETE CASCADE
            );
            """;

        var model = await BuildAsync(sql);

        var fk = Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlForeignKeyConstraint);
        Assert.Equal("fk_customer", fk.Name);
        Assert.Equal("Cascade", fk.GetProperty<string>(MariaDbPropertyNames.DeleteAction));

        var foreignTable = Assert.IsType<Reference>(
            fk.GetRelationship(MariaDbRelationshipNames.ForeignTable)!.Entries[0]);
        Assert.Equal("customer", foreignTable.Name);
    }

    [Fact]
    public async Task ExtractModel_UnnamedForeignKey_UsesIbfkConvention()
    {
        const string sql = """
            CREATE TABLE customer
            (
                id int NOT NULL PRIMARY KEY
            );
            CREATE TABLE orders
            (
                id int NOT NULL PRIMARY KEY,
                customer_id int NOT NULL,
                FOREIGN KEY (customer_id) REFERENCES customer (id)
            );
            """;

        var model = await BuildAsync(sql);

        var fk = Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlForeignKeyConstraint);
        Assert.Equal("orders_ibfk_1", fk.Name);
    }

    [Fact]
    public async Task ExtractModel_CreateIndex_YieldsIndexElement()
    {
        const string sql = """
            CREATE TABLE foo
            (
                id int NOT NULL PRIMARY KEY,
                name varchar(50) NOT NULL
            );
            CREATE INDEX ix_foo_name ON foo (name);
            """;

        var model = await BuildAsync(sql);

        var index = Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlIndex);
        Assert.Equal("ix_foo_name", index.Name);
        Assert.Equal(false, index.GetProperty<bool?>(MariaDbPropertyNames.IsUnique));

        var indexedObject = Assert.IsType<Reference>(
            index.GetRelationship(MariaDbRelationshipNames.IndexedObject)!.Entries[0]);
        Assert.Equal("foo", indexedObject.Name);
    }

    [Fact]
    public async Task ExtractModel_UniqueColumnConstraint_YieldsUniqueIndex()
    {
        const string sql = """
            CREATE TABLE foo
            (
                id int NOT NULL PRIMARY KEY,
                email varchar(255) NOT NULL UNIQUE
            );
            """;

        var model = await BuildAsync(sql);

        var index = Assert.Single(model.Elements, i => i.Type == MariaDbElementTypes.SqlIndex);
        Assert.Equal(true, index.GetProperty<bool?>(MariaDbPropertyNames.IsUnique));
        Assert.Equal("email", index.Name);
    }
}

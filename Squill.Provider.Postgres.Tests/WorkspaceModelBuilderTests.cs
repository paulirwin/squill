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

        // The table carries a Schema relationship (implicit "public") plus its Columns,
        // matching the DB builder so a parsed model hash-matches an extracted one.
        Assert.Equal(2, table.Relationships.Count);
        var schema = table.Relationships[0];
        Assert.Equal(PostgresRelationshipNames.Schema, schema.Name);
        var schemaRef = Assert.IsType<Reference>(Assert.Single(schema.Entries));
        Assert.Equal("public", schemaRef.Name);

        var columns = table.Relationships[1];
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
    public async Task ExtractModel_BareVarchar_HasNoLengthOrMaxProperties()
    {
        // A bare `varchar` (no length) must produce the same type-specifier shape as
        // the DB builder, which reports character_maximum_length = NULL and so emits
        // neither a Length nor an IsMax property. Emitting IsMax here would break
        // parser-vs-DB hash equality (issue #6).
        const string sql = """
CREATE TABLE notes
(
    body varchar
);
""";

        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Notes.sql", FileKind.Compile, sql));

        var model = await new ParserWorkspaceModelBuilder(workspace, parser)
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        var table = model.Elements.Single(i => i.Type == PostgresElementTypes.SqlTable);
        var columns = table.Relationships.Single(r => r.Name == PostgresRelationshipNames.Columns);
        var bodyCol = Assert.IsType<Element>(columns.Entries[0]);

        var typeElem = Assert.IsType<Element>(
            bodyCol.Relationships.Single(r => r.Name == PostgresRelationshipNames.TypeSpecifier).Entries[0]);

        var typeRef = Assert.IsType<Reference>(
            typeElem.Relationships.Single(r => r.Name == PostgresRelationshipNames.Type).Entries[0]);
        Assert.Equal("character varying", typeRef.Name);

        // No Length and no IsMax on a bare varchar — matches the DB builder exactly.
        Assert.Empty(typeElem.Properties);
    }

    [Fact]
    public async Task ExtractModel_Numeric_HasPrecisionAndScale()
    {
        // A `numeric(p, s)` column must produce Precision/Scale properties on its
        // type specifier, matching what the DB builder extracts, so parser-vs-DB
        // hashes agree (issue #33). The parser stores both as long.
        const string sql = """
CREATE TABLE prices
(
    amount numeric(12, 2)
);
""";

        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Prices.sql", FileKind.Compile, sql));

        var model = await new ParserWorkspaceModelBuilder(workspace, parser)
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        var table = model.Elements.Single(i => i.Type == PostgresElementTypes.SqlTable);
        var columns = table.Relationships.Single(r => r.Name == PostgresRelationshipNames.Columns);
        var amountCol = Assert.IsType<Element>(columns.Entries[0]);

        var typeElem = Assert.IsType<Element>(
            amountCol.Relationships.Single(r => r.Name == PostgresRelationshipNames.TypeSpecifier).Entries[0]);

        var typeRef = Assert.IsType<Reference>(
            typeElem.Relationships.Single(r => r.Name == PostgresRelationshipNames.Type).Entries[0]);
        Assert.Equal("numeric", typeRef.Name);

        Assert.Equal(12L, typeElem.GetProperty<long?>(PostgresPropertyNames.Precision));
        Assert.Equal(2L, typeElem.GetProperty<long?>(PostgresPropertyNames.Scale));
        Assert.Null(typeElem.GetProperty<int?>(PostgresPropertyNames.Length));
    }

    [Fact]
    public async Task ExtractModel_IdentityColumnTest()
    {
        const string sql = """
CREATE TABLE widgets
(
    id integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    serial_no integer GENERATED ALWAYS AS IDENTITY
);
""";

        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Widgets.sql", FileKind.Compile, sql));

        var builder = new ParserWorkspaceModelBuilder(workspace, parser);

        var model = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var table = model.Elements.Single(i => i.Type == PostgresElementTypes.SqlTable);
        var columns = table.Relationships.Single(r => r.Name == PostgresRelationshipNames.Columns);

        var idCol = Assert.IsType<Element>(columns.Entries[0]);
        Assert.Equal("widgets.id", idCol.Name);
        Assert.Equal(true, idCol.GetProperty<bool?>(PostgresPropertyNames.IsIdentity));
        Assert.Equal("ByDefault", idCol.GetProperty<string>(PostgresPropertyNames.IdentityGeneration));
        Assert.Equal(false, idCol.GetProperty<bool?>(PostgresPropertyNames.IsNullable));
        // Identity + PRIMARY KEY must not emit a duplicate IsNullable property.
        Assert.Single(idCol.Properties, p => p.Name == PostgresPropertyNames.IsNullable);

        var serialCol = Assert.IsType<Element>(columns.Entries[1]);
        Assert.Equal("widgets.serial_no", serialCol.Name);
        Assert.Equal(true, serialCol.GetProperty<bool?>(PostgresPropertyNames.IsIdentity));
        Assert.Equal("Always", serialCol.GetProperty<string>(PostgresPropertyNames.IdentityGeneration));
        Assert.Equal(false, serialCol.GetProperty<bool?>(PostgresPropertyNames.IsNullable));
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
        // With USING omitted, the index method defaults to btree — matching the DB builder,
        // which reads "btree" from pg_am for a plain index.
        Assert.Equal("btree", index.GetProperty<string>(PostgresPropertyNames.IndexMethod));

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

        // A btree index column with no explicit ASC/DESC or NULLS carries Postgres's
        // implicit defaults (ASC, NULLS LAST), recorded so a parsed index hash-matches
        // one extracted from the database.
        Assert.Equal(true, columnSpec.GetProperty<bool?>(PostgresPropertyNames.IsAscending));
        Assert.Equal(false, columnSpec.GetProperty<bool?>(PostgresPropertyNames.NullsFirst));
    }

    [Fact]
    public async Task ExtractModel_UniqueIndexWithMethodDirectionAndNullOrderTest()
    {
        const string sql = """
CREATE TABLE users
(
    id integer PRIMARY KEY,
    email varchar(200) NOT NULL
);
CREATE UNIQUE INDEX idx_email ON users USING btree (email DESC NULLS LAST);
""";

        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("UsersEmailIndex.sql", FileKind.Compile, sql));

        var builder = new ParserWorkspaceModelBuilder(workspace, parser);

        var model = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var index = Assert.Single(model.Elements, e => e.Type == PostgresElementTypes.SqlIndex);
        Assert.Equal("idx_email", index.Name);
        Assert.Equal(true, index.GetProperty<bool?>(PostgresPropertyNames.IsUnique));
        Assert.Equal("btree", index.GetProperty<string>(PostgresPropertyNames.IndexMethod));

        var columnSpecs = index.GetRelationship(PostgresRelationshipNames.ColumnSpecifications);
        Assert.NotNull(columnSpecs);
        var columnSpec = Assert.IsType<Element>(Assert.Single(columnSpecs.Entries));
        Assert.Equal(false, columnSpec.GetProperty<bool?>(PostgresPropertyNames.IsAscending));
        Assert.Equal(false, columnSpec.GetProperty<bool?>(PostgresPropertyNames.NullsFirst));
    }

    [Fact]
    public async Task ExtractModel_PartialIndexTest()
    {
        const string sql = """
CREATE TABLE users
(
    id    integer PRIMARY KEY,
    email varchar(255)
);

CREATE INDEX idx_email ON users (email) WHERE email IS NOT NULL;
""";

        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Users.sql", FileKind.Compile, sql));

        var builder = new ParserWorkspaceModelBuilder(workspace, parser);

        var model = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var index = model.Elements.Single(i => i.Type == PostgresElementTypes.SqlIndex);
        Assert.Equal("idx_email", index.Name);
        Assert.Equal("\"email\" IS NOT NULL", index.GetProperty<string>(PostgresPropertyNames.FilterPredicate));
    }

    [Fact]
    public async Task ExtractModel_FullIndex_HasNoFilterPredicate()
    {
        const string sql = """
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);

CREATE INDEX idx_title ON film (title);
""";

        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Film.sql", FileKind.Compile, sql));

        var builder = new ParserWorkspaceModelBuilder(workspace, parser);

        var model = await builder.ExtractModelAsync(TestContext.Current.CancellationToken);

        var index = model.Elements.Single(i => i.Type == PostgresElementTypes.SqlIndex);
        Assert.Null(index.GetProperty<string>(PostgresPropertyNames.FilterPredicate));
    }

    [Fact]
    public async Task ExtractModel_InlineForeignKeyWithOnDeleteCascade()
    {
        const string sql = """
CREATE TABLE customers
(
    id integer PRIMARY KEY
);

CREATE TABLE orders
(
    id          integer PRIMARY KEY,
    customer_id integer NOT NULL REFERENCES customers (id) ON DELETE CASCADE
);
""";

        var model = await BuildModel(sql);

        var fk = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlForeignKeyConstraint);

        // Postgres convention for an unnamed inline FK: <table>_<column>_fkey.
        Assert.Equal("orders_customer_id_fkey", fk.Name);

        var definingTable = fk.GetRelationship(PostgresRelationshipNames.DefiningTable);
        Assert.Equal("orders", Assert.IsType<Reference>(Assert.Single(definingTable!.Entries)).Name);

        var fkColumns = fk.GetRelationship(PostgresRelationshipNames.ForeignKeyColumns);
        Assert.Equal("orders.customer_id", Assert.IsType<Reference>(Assert.Single(fkColumns!.Entries)).Name);

        var foreignTable = fk.GetRelationship(PostgresRelationshipNames.ForeignTable);
        Assert.Equal("customers", Assert.IsType<Reference>(Assert.Single(foreignTable!.Entries)).Name);

        var foreignColumns = fk.GetRelationship(PostgresRelationshipNames.ForeignColumns);
        Assert.Equal("customers.id", Assert.IsType<Reference>(Assert.Single(foreignColumns!.Entries)).Name);

        Assert.Equal("Cascade", fk.GetProperty<string>(PostgresPropertyNames.DeleteAction));
        Assert.Null(fk.GetProperty<string>(PostgresPropertyNames.UpdateAction));
    }

    [Fact]
    public async Task ExtractModel_TableLevelCompositeForeignKey()
    {
        const string sql = """
CREATE TABLE orders
(
    id      integer NOT NULL,
    line_no integer NOT NULL,
    PRIMARY KEY (id, line_no)
);
CREATE TABLE order_lines
(
    order_id integer NOT NULL,
    line_no  integer NOT NULL,
    CONSTRAINT fk_lines FOREIGN KEY (order_id, line_no) REFERENCES orders (id, line_no)
);
""";

        var model = await BuildModel(sql);

        var fk = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlForeignKeyConstraint);

        // Explicitly named constraint keeps its name.
        Assert.Equal("fk_lines", fk.Name);

        var fkColumns = fk.GetRelationship(PostgresRelationshipNames.ForeignKeyColumns)!;
        Assert.Equal(
            new[] { "order_lines.order_id", "order_lines.line_no" },
            fkColumns.Entries.OfType<Reference>().Select(r => r.Name));

        var foreignColumns = fk.GetRelationship(PostgresRelationshipNames.ForeignColumns)!;
        Assert.Equal(
            new[] { "orders.id", "orders.line_no" },
            foreignColumns.Entries.OfType<Reference>().Select(r => r.Name));

        // No ON DELETE/UPDATE specified -> both action properties absent (NO ACTION default).
        Assert.Null(fk.GetProperty<string>(PostgresPropertyNames.DeleteAction));
        Assert.Null(fk.GetProperty<string>(PostgresPropertyNames.UpdateAction));
    }

    [Fact]
    public async Task ExtractModel_TableLevelCompositePrimaryKey()
    {
        const string sql = """
CREATE TABLE order_lines
(
    order_id integer NOT NULL,
    line_no  integer NOT NULL,
    PRIMARY KEY (order_id, line_no)
);
""";

        var model = await BuildModel(sql);

        // table + standalone composite primary-key constraint element.
        Assert.Equal(2, model.Elements.Count);

        var pk = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlPrimaryKeyConstraint);

        // Postgres names an unnamed table-level PK <table>_pkey.
        Assert.Equal("order_lines_pkey", pk.Name);

        var definingTable = pk.GetRelationship(PostgresRelationshipNames.DefiningTable);
        Assert.Equal("order_lines", Assert.IsType<Reference>(Assert.Single(definingTable!.Entries)).Name);

        // Both key columns are present, in declaration order.
        Assert.Equal(
            new[] { "order_lines.order_id", "order_lines.line_no" },
            PrimaryKeyColumnReferences(pk));
    }

    [Fact]
    public async Task ExtractModel_NamedTableLevelCompositePrimaryKey()
    {
        const string sql = """
CREATE TABLE order_lines
(
    order_id integer NOT NULL,
    line_no  integer NOT NULL,
    CONSTRAINT pk_order_lines PRIMARY KEY (order_id, line_no)
);
""";

        var model = await BuildModel(sql);

        var pk = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlPrimaryKeyConstraint);

        // Explicitly named constraint keeps its name.
        Assert.Equal("pk_order_lines", pk.Name);

        Assert.Equal(
            new[] { "order_lines.order_id", "order_lines.line_no" },
            PrimaryKeyColumnReferences(pk));
    }

    private static IEnumerable<string> PrimaryKeyColumnReferences(Element primaryKey)
    {
        var columnSpecs = primaryKey.GetRelationship(PostgresRelationshipNames.ColumnSpecifications)!;

        return columnSpecs.Entries
            .OfType<Element>()
            .Select(spec => Assert.IsType<Reference>(
                Assert.Single(spec.GetRelationship(PostgresRelationshipNames.Column)!.Entries)).Name);
    }

    [Fact]
    public async Task ExtractModel_CreateExtensionTest()
    {
        var model = await BuildModel("CREATE EXTENSION citext;");

        var extension = Assert.Single(model.Elements);
        Assert.Equal(PostgresElementTypes.SqlExtension, extension.Type);
        Assert.Equal("citext", extension.Name);
        // No version declared, so no Version property (name is the identity).
        Assert.Null(extension.GetProperty<string>(PostgresPropertyNames.Version));
    }

    [Fact]
    public async Task ExtractModel_CreateExtensionIfNotExistsTest()
    {
        var model = await BuildModel("CREATE EXTENSION IF NOT EXISTS vector;");

        var extension = Assert.Single(model.Elements);
        Assert.Equal(PostgresElementTypes.SqlExtension, extension.Type);
        Assert.Equal("vector", extension.Name);
    }

    [Fact]
    public async Task ExtractModel_CreateExtensionWithVersionTest()
    {
        var model = await BuildModel("CREATE EXTENSION citext WITH VERSION '1.6';");

        var extension = Assert.Single(model.Elements);
        Assert.Equal(PostgresElementTypes.SqlExtension, extension.Type);
        Assert.Equal("citext", extension.Name);
        Assert.Equal("1.6", extension.GetProperty<string>(PostgresPropertyNames.Version));
    }

    [Fact]
    public async Task ExtractModel_VectorColumn_StoresTypeNameAndDimension()
    {
        var model = await BuildModel("""
CREATE TABLE items
(
    embedding vector(3)
);
""");

        var table = model.Elements.Single(i => i.Type == PostgresElementTypes.SqlTable);
        var columns = table.Relationships.Single(r => r.Name == PostgresRelationshipNames.Columns);
        var embeddingCol = Assert.IsType<Element>(Assert.Single(columns.Entries));

        var typeElem = Assert.IsType<Element>(
            embeddingCol.Relationships.Single(r => r.Name == PostgresRelationshipNames.TypeSpecifier).Entries[0]);

        var typeRef = Assert.IsType<Reference>(
            typeElem.Relationships.Single(r => r.Name == PostgresRelationshipNames.Type).Entries[0]);
        // The custom type name is carried verbatim (matching the DB builder's udt_name).
        Assert.Equal("vector", typeRef.Name);

        // The dimension is modeled as Length, the same property the DB builder reports.
        Assert.Equal(3, typeElem.GetProperty<int?>(PostgresPropertyNames.Length));
    }

    [Fact]
    public async Task ExtractModel_HnswIndex_StoresOperatorClassAndStorageParameters()
    {
        var model = await BuildModel("""
CREATE TABLE items
(
    embedding vector(3)
);

CREATE INDEX items_embedding_idx ON items USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);
""");

        var index = model.Elements.Single(i => i.Type == PostgresElementTypes.SqlIndex);
        Assert.Equal("items_embedding_idx", index.Name);
        Assert.Equal("hnsw", index.GetProperty<string>(PostgresPropertyNames.IndexMethod));
        Assert.Equal("m=16, ef_construction=64", index.GetProperty<string>(PostgresPropertyNames.StorageParameters));

        var columnSpecs = index.GetRelationship(PostgresRelationshipNames.ColumnSpecifications);
        Assert.NotNull(columnSpecs);
        var columnSpec = Assert.IsType<Element>(Assert.Single(columnSpecs.Entries));
        Assert.Equal("vector_cosine_ops", columnSpec.GetProperty<string>(PostgresPropertyNames.OperatorClass));

        // A non-btree access method (hnsw) rejects ASC/DESC and NULLS ordering, so neither
        // builder records those defaults — leaving them out keeps the model free of
        // ordering the emitted DDL can't legally carry.
        Assert.Null(columnSpec.GetProperty<bool?>(PostgresPropertyNames.IsAscending));
        Assert.Null(columnSpec.GetProperty<bool?>(PostgresPropertyNames.NullsFirst));
    }

    private static async Task<Model> BuildModel(string sql)
    {
        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        var builder = new ParserWorkspaceModelBuilder(workspace, parser);

        return await builder.ExtractModelAsync(TestContext.Current.CancellationToken);
    }
}

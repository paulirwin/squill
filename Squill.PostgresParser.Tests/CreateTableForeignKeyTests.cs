using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

public class CreateTableForeignKeyTests
{
    [Fact]
    public void InlineReferences_WithoutColumnOrActions()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE orders
(
    id          integer PRIMARY KEY,
    customer_id integer REFERENCES customers
);
""";

        var createTable = ParseSingleCreateTable(parser, text);

        var customerColumn = Assert.IsType<ColumnDefinition>(createTable.Elements[1]);
        Assert.Equal("customer_id", customerColumn.Name.Name);

        var fk = Assert.Single(customerColumn.Constraints.OfType<ForeignKeyColumnConstraint>());
        Assert.Equal("customers", fk.ReferencedTable.ToString());
        Assert.Null(fk.ReferencedColumn);
        Assert.Null(fk.OnDelete);
        Assert.Null(fk.OnUpdate);
    }

    [Fact]
    public void InlineReferences_WithColumnAndOnDeleteCascade()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE orders
(
    id          integer PRIMARY KEY,
    customer_id integer REFERENCES customers (id) ON DELETE CASCADE
);
""";

        var createTable = ParseSingleCreateTable(parser, text);

        var customerColumn = Assert.IsType<ColumnDefinition>(createTable.Elements[1]);

        var fk = Assert.Single(customerColumn.Constraints.OfType<ForeignKeyColumnConstraint>());
        Assert.Equal("customers", fk.ReferencedTable.ToString());
        Assert.NotNull(fk.ReferencedColumn);
        Assert.Equal("id", fk.ReferencedColumn.Name);
        Assert.Equal(ReferentialAction.Cascade, fk.OnDelete);
        Assert.Null(fk.OnUpdate);
    }

    [Fact]
    public void InlineReferences_WithOnDeleteAndOnUpdate()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE orders
(
    id          integer PRIMARY KEY,
    customer_id integer REFERENCES customers (id) ON DELETE SET NULL ON UPDATE RESTRICT
);
""";

        var createTable = ParseSingleCreateTable(parser, text);

        var customerColumn = Assert.IsType<ColumnDefinition>(createTable.Elements[1]);
        var fk = Assert.Single(customerColumn.Constraints.OfType<ForeignKeyColumnConstraint>());

        Assert.Equal(ReferentialAction.SetNull, fk.OnDelete);
        Assert.Equal(ReferentialAction.Restrict, fk.OnUpdate);
    }

    [Fact]
    public void NamedInlineReferences()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE orders
(
    id          integer PRIMARY KEY,
    customer_id integer CONSTRAINT FK_orders_customers REFERENCES customers (id) ON DELETE CASCADE
);
""";

        var createTable = ParseSingleCreateTable(parser, text);

        var customerColumn = Assert.IsType<ColumnDefinition>(createTable.Elements[1]);

        var named = Assert.Single(customerColumn.Constraints.OfType<NamedColumnConstraint>());
        Assert.Equal("FK_orders_customers", named.Name);

        var fk = Assert.IsType<ForeignKeyColumnConstraint>(named.Constraint);
        Assert.Equal("customers", fk.ReferencedTable.ToString());
        Assert.Equal(ReferentialAction.Cascade, fk.OnDelete);
    }

    [Fact]
    public void TableLevelForeignKey_SingleColumn()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE orders
(
    id          integer PRIMARY KEY,
    customer_id integer NOT NULL,
    FOREIGN KEY (customer_id) REFERENCES customers (id) ON DELETE CASCADE
);
""";

        var createTable = ParseSingleCreateTable(parser, text);

        var fk = Assert.Single(createTable.Elements.OfType<ForeignKeyTableConstraint>());
        Assert.Equal(new[] { "customer_id" }, fk.Columns.Select(c => c.Name));
        Assert.Equal("customers", fk.ReferencedTable.ToString());
        Assert.Equal(new[] { "id" }, fk.ReferencedColumns.Select(c => c.Name));
        Assert.Equal(ReferentialAction.Cascade, fk.OnDelete);
    }

    [Fact]
    public void TableLevelForeignKey_CompositeColumns()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE order_lines
(
    order_id integer NOT NULL,
    line_no  integer NOT NULL,
    FOREIGN KEY (order_id, line_no) REFERENCES orders (id, line_no)
);
""";

        var createTable = ParseSingleCreateTable(parser, text);

        var fk = Assert.Single(createTable.Elements.OfType<ForeignKeyTableConstraint>());
        Assert.Equal(new[] { "order_id", "line_no" }, fk.Columns.Select(c => c.Name));
        Assert.Equal("orders", fk.ReferencedTable.ToString());
        Assert.Equal(new[] { "id", "line_no" }, fk.ReferencedColumns.Select(c => c.Name));
        Assert.Null(fk.OnDelete);
        Assert.Null(fk.OnUpdate);
    }

    [Fact]
    public void NamedTableLevelForeignKey()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE orders
(
    id          integer PRIMARY KEY,
    customer_id integer NOT NULL,
    CONSTRAINT FK_orders_customers FOREIGN KEY (customer_id) REFERENCES customers (id)
);
""";

        var createTable = ParseSingleCreateTable(parser, text);

        var named = Assert.Single(createTable.Elements.OfType<NamedTableConstraint>());
        Assert.Equal("FK_orders_customers", named.Name.Name);

        var fk = Assert.IsType<ForeignKeyTableConstraint>(named.Constraint);
        Assert.Equal(new[] { "customer_id" }, fk.Columns.Select(c => c.Name));
        Assert.Equal("customers", fk.ReferencedTable.ToString());
    }

    [Fact]
    public void TableLevelPrimaryKey_Composite()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE order_lines
(
    order_id integer NOT NULL,
    line_no  integer NOT NULL,
    PRIMARY KEY (order_id, line_no)
);
""";

        var createTable = ParseSingleCreateTable(parser, text);

        var pk = Assert.Single(createTable.Elements.OfType<PrimaryKeyTableConstraint>());
        Assert.Equal(new[] { "order_id", "line_no" }, pk.Columns.Select(c => c.Name));
    }

    [Fact]
    public void NamedTableLevelPrimaryKey()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE order_lines
(
    order_id integer NOT NULL,
    line_no  integer NOT NULL,
    CONSTRAINT pk_order_lines PRIMARY KEY (order_id, line_no)
);
""";

        var createTable = ParseSingleCreateTable(parser, text);

        var named = Assert.Single(createTable.Elements.OfType<NamedTableConstraint>());
        Assert.Equal("pk_order_lines", named.Name.Name);
        Assert.IsType<PrimaryKeyTableConstraint>(named.Constraint);
    }

    private static CreateTableStatement ParseSingleCreateTable(AntlrPostgresParser parser, string text)
    {
        var root = parser.Parse(text);

        Assert.NotNull(root);
        Assert.Single(root.Statements);

        return Assert.IsType<CreateTableStatement>(root.Statements[0]);
    }
}

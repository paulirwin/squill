using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// UNIQUE constraint parsing, at both the column and table level (issue #121).
/// </summary>
public class CreateTableUniqueTests
{
    [Fact]
    public void CreateTable_ColumnLevelUnique()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE Foo
(
    id integer PRIMARY KEY,
    email varchar(255) UNIQUE
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        var emailColumn = Assert.IsType<ColumnDefinition>(createTable.Elements[1]);

        Assert.Equal("email", emailColumn.Name.Name);
        Assert.IsType<UniqueColumnConstraint>(Assert.Single(emailColumn.Constraints));
    }

    [Fact]
    public void CreateTable_ColumnLevelUnique_WithNotNull()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE Foo
(
    email varchar(255) NOT NULL UNIQUE
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        var emailColumn = Assert.IsType<ColumnDefinition>(Assert.Single(createTable.Elements));

        Assert.Equal(2, emailColumn.Constraints.Count);

        var nullable = Assert.IsType<NullableColumnConstraint>(emailColumn.Constraints[0]);
        Assert.False(nullable.Nullable);

        Assert.IsType<UniqueColumnConstraint>(emailColumn.Constraints[1]);
    }

    [Fact]
    public void CreateTable_NamedColumnLevelUnique()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE Foo
(
    email varchar(255) CONSTRAINT UQ_Foo_Email UNIQUE
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        var emailColumn = Assert.IsType<ColumnDefinition>(Assert.Single(createTable.Elements));

        var namedConstraint = Assert.IsType<NamedColumnConstraint>(Assert.Single(emailColumn.Constraints));

        Assert.Equal("UQ_Foo_Email", namedConstraint.Name);
        Assert.IsType<UniqueColumnConstraint>(namedConstraint.Constraint);
    }

    [Fact]
    public void CreateTable_TableLevelUnique_SingleColumn()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE Foo
(
    id integer PRIMARY KEY,
    email varchar(255),
    UNIQUE (email)
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        Assert.Equal(3, createTable.Elements.Count);

        var unique = Assert.IsType<UniqueTableConstraint>(createTable.Elements[2]);

        Assert.Equal(["email"], unique.Columns.Select(c => c.Name));
    }

    [Fact]
    public void CreateTable_TableLevelUnique_Composite()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE Foo
(
    id integer PRIMARY KEY,
    tenant_id integer NOT NULL,
    email varchar(255) NOT NULL,
    UNIQUE (tenant_id, email)
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        var unique = Assert.IsType<UniqueTableConstraint>(createTable.Elements[3]);

        Assert.Equal(["tenant_id", "email"], unique.Columns.Select(c => c.Name));
    }

    [Fact]
    public void CreateTable_NamedTableLevelUnique()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE Foo
(
    id integer PRIMARY KEY,
    email varchar(255),
    CONSTRAINT UQ_Foo_Email UNIQUE (email)
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        var named = Assert.IsType<NamedTableConstraint>(createTable.Elements[2]);

        Assert.Equal("UQ_Foo_Email", named.Name.Name);

        var unique = Assert.IsType<UniqueTableConstraint>(named.Constraint);

        Assert.Equal(["email"], unique.Columns.Select(c => c.Name));
    }
}

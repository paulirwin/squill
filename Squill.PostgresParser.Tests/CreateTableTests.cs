using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

public class CreateTableTests
{
    [Fact]
    public void CreateTable_SimpleHappyPath()
    {
        var parser = new AntlrPostgresParser();
        
        // TODO: move to embedded resource
        const string text = """
CREATE TABLE Foo 
(
    id integer PRIMARY KEY,
    name varchar(100) NOT NULL
);
""";

        var root = parser.Parse(text);
        
        Assert.NotNull(root);
        Assert.Equal(1, root.Statements.Count);

        var createTable = Assert.IsType<CreateTableStatement>(root.Statements[0]);
        
        Assert.Equal("Foo", createTable.Name.ToString());
        Assert.Equal(2, createTable.Elements.Count);

        var idColumn = Assert.IsType<ColumnDefinition>(createTable.Elements[0]);
        var nameColumn = Assert.IsType<ColumnDefinition>(createTable.Elements[1]);
        
        Assert.Equal("id", idColumn.Name);
        Assert.Equal("integer", idColumn.DataType.TypeName);
        Assert.Equal(1, idColumn.Constraints.Count);
        Assert.IsType<PrimaryKeyColumnConstraint>(idColumn.Constraints[0]);
        
        Assert.Equal("name", nameColumn.Name);
        Assert.Equal("varchar", nameColumn.DataType.TypeName);
        Assert.Equal(1, nameColumn.DataType.Modifiers.Count);
        Assert.Equivalent(100, nameColumn.DataType.Modifiers[0]);
        Assert.Equal(1, nameColumn.Constraints.Count);
        var nullableConstraint = Assert.IsType<NullableColumnConstraint>(nameColumn.Constraints[0]);
        Assert.False(nullableConstraint.Nullable);
    }

    [Fact]
    public void CreateTable_NamedColumnConstraintTest()
    {
        var parser = new AntlrPostgresParser();
        
        // TODO: move to embedded resource
        const string text = """
CREATE TABLE Foo 
(
    id integer CONSTRAINT PK_Foo PRIMARY KEY
);
""";

        var root = parser.Parse(text);
        
        Assert.NotNull(root);
        Assert.Equal(1, root.Statements.Count);

        var createTable = Assert.IsType<CreateTableStatement>(root.Statements[0]);
        
        Assert.Equal("Foo", createTable.Name.ToString());
        Assert.Equal(1, createTable.Elements.Count);

        var idColumn = Assert.IsType<ColumnDefinition>(createTable.Elements[0]);
        
        Assert.Equal("id", idColumn.Name);
        Assert.Equal("integer", idColumn.DataType.TypeName);
        Assert.Equal(1, idColumn.Constraints.Count);
        
        var namedConstraint = Assert.IsType<NamedColumnConstraint>(idColumn.Constraints[0]);
        
        Assert.Equal("PK_Foo", namedConstraint.Name);
        Assert.IsType<PrimaryKeyColumnConstraint>(namedConstraint.Constraint);
    }
}
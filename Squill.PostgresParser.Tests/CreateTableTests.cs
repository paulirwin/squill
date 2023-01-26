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

    /// <summary>
    /// A test for the Sakila sample database `actor` table. See license in README.md
    /// </summary>
    [Fact]
    public void Sakila_ActorTableTest()
    {
        var parser = new AntlrPostgresParser();
        
        // TODO: move to embedded resource
        const string text = """
CREATE TABLE actor (
    actor_id integer DEFAULT nextval('actor_actor_id_seq'::regclass) NOT NULL,
    first_name character varying(45) NOT NULL,
    last_name character varying(45) NOT NULL,
    last_update timestamp without time zone DEFAULT now() NOT NULL
);
""";

        var root = parser.Parse(text);
        Assert.NotNull(root);
        Assert.Equal(1, root.Statements.Count);

        var createTable = Assert.IsType<CreateTableStatement>(root.Statements[0]);
        
        Assert.Equal("actor", createTable.Name.ToString());
        Assert.Equal(4, createTable.Elements.Count);

        var idColumn = Assert.IsType<ColumnDefinition>(createTable.Elements[0]);
        
        Assert.Equal("actor_id", idColumn.Name);
        Assert.Equal("integer", idColumn.DataType.TypeName);
        Assert.Equal(2, idColumn.Constraints.Count);

        var idDefault = Assert.IsType<DefaultColumnConstraint>(idColumn.Constraints[0]);
        var nextvalFunc = Assert.IsType<FunctionApplicationExpression>(idDefault.Expression);
        Assert.Equal("nextval", nextvalFunc.Name);
        Assert.Equal(1, nextvalFunc.Arguments.Count);
        var typecastExpr = Assert.IsType<TypecastExpression>(nextvalFunc.Arguments[0].Expression);
        var stringLiteral = Assert.IsType<LiteralExpression>(typecastExpr.Expression);
        Assert.Equal("actor_actor_id_seq", stringLiteral.Value);
        var objIdType = Assert.IsType<ObjectIdentifierTypeName>(typecastExpr.DataType);
        Assert.Equal(PostgresObjectIdentifierTypes.Regclass, objIdType.ObjectIdentifierType);

        var nullableConstraint = Assert.IsType<NullableColumnConstraint>(idColumn.Constraints[1]);
        Assert.False(nullableConstraint.Nullable);

        var firstNameColumn = Assert.IsType<ColumnDefinition>(createTable.Elements[1]);
        
        Assert.Equal("first_name", firstNameColumn.Name);
        var firstNameType = Assert.IsType<BuiltInDataType>(firstNameColumn.DataType);
        Assert.Equal(PostgresBuiltInDataType.Varchar, firstNameType.Type);
        Assert.Equal(1, firstNameType.Modifiers.Count);
        Assert.Equivalent(45, firstNameType.Modifiers[0]);
        Assert.Equal(1, firstNameColumn.Constraints.Count);
        nullableConstraint = Assert.IsType<NullableColumnConstraint>(firstNameColumn.Constraints[0]);
        Assert.False(nullableConstraint.Nullable);
        
        var lastNameColumn = Assert.IsType<ColumnDefinition>(createTable.Elements[2]);
        
        Assert.Equal("last_name", lastNameColumn.Name);
        var lastNameType = Assert.IsType<BuiltInDataType>(lastNameColumn.DataType);
        Assert.Equal(PostgresBuiltInDataType.Varchar, lastNameType.Type);
        Assert.Equal(1, lastNameType.Modifiers.Count);
        Assert.Equivalent(45, lastNameType.Modifiers[0]);
        Assert.Equal(1, lastNameColumn.Constraints.Count);
        nullableConstraint = Assert.IsType<NullableColumnConstraint>(lastNameColumn.Constraints[0]);
        Assert.False(nullableConstraint.Nullable);

        AssertSakilaLastUpdateColumn(createTable, 3);
    }

    private static void AssertSakilaLastUpdateColumn(CreateTableStatement createTable, int columnIndex)
    {
        var lastUpdateColumn = Assert.IsType<ColumnDefinition>(createTable.Elements[columnIndex]);

        Assert.Equal("last_update", lastUpdateColumn.Name);
        
        var lastUpdateType = Assert.IsType<BuiltInDataType>(lastUpdateColumn.DataType);
        Assert.Equal(PostgresBuiltInDataType.Timestamp, lastUpdateType.Type);
        Assert.Equal(2, lastUpdateColumn.Constraints.Count);
        
        var lastUpdateDefault = Assert.IsType<DefaultColumnConstraint>(lastUpdateColumn.Constraints[0]);
        var nowFunc = Assert.IsType<FunctionApplicationExpression>(lastUpdateDefault.Expression);
        Assert.Equal("now", nowFunc.Name);
        Assert.Equal(0, nowFunc.Arguments.Count);
        
        var nullableConstraint = Assert.IsType<NullableColumnConstraint>(lastUpdateColumn.Constraints[1]);
        Assert.False(nullableConstraint.Nullable);
    }

    // <summary>
    /// A test for the Sakila sample database `category` table. See license in README.md
    /// </summary>
    [Fact]
    public void Sakila_CategoryTableTest()
    {
        var parser = new AntlrPostgresParser();

        // TODO: move to embedded resource
        const string text = """
CREATE TABLE category (
    category_id integer DEFAULT nextval('category_category_id_seq'::regclass) NOT NULL,
    name character varying(25) NOT NULL,
    last_update timestamp without time zone DEFAULT now() NOT NULL
);
""";

        var root = parser.Parse(text);
        Assert.NotNull(root);
        Assert.Equal(1, root.Statements.Count);

        var createTable = Assert.IsType<CreateTableStatement>(root.Statements[0]);

        Assert.Equal("category", createTable.Name.ToString());
        Assert.Equal(3, createTable.Elements.Count);
        
        var idColumn = Assert.IsType<ColumnDefinition>(createTable.Elements[0]);
        
        Assert.Equal("category_id", idColumn.Name);
        Assert.Equal("integer", idColumn.DataType.TypeName);
        Assert.Equal(2, idColumn.Constraints.Count);

        var idDefault = Assert.IsType<DefaultColumnConstraint>(idColumn.Constraints[0]);
        var nextvalFunc = Assert.IsType<FunctionApplicationExpression>(idDefault.Expression);
        Assert.Equal("nextval", nextvalFunc.Name);
        Assert.Equal(1, nextvalFunc.Arguments.Count);
        var typecastExpr = Assert.IsType<TypecastExpression>(nextvalFunc.Arguments[0].Expression);
        var stringLiteral = Assert.IsType<LiteralExpression>(typecastExpr.Expression);
        Assert.Equal("category_category_id_seq", stringLiteral.Value);
        var objIdType = Assert.IsType<ObjectIdentifierTypeName>(typecastExpr.DataType);
        Assert.Equal(PostgresObjectIdentifierTypes.Regclass, objIdType.ObjectIdentifierType);

        var nullableConstraint = Assert.IsType<NullableColumnConstraint>(idColumn.Constraints[1]);
        Assert.False(nullableConstraint.Nullable);

        var nameColumn = Assert.IsType<ColumnDefinition>(createTable.Elements[1]);
        
        Assert.Equal("name", nameColumn.Name);
        var firstNameType = Assert.IsType<BuiltInDataType>(nameColumn.DataType);
        Assert.Equal(PostgresBuiltInDataType.Varchar, firstNameType.Type);
        Assert.Equal(1, firstNameType.Modifiers.Count);
        Assert.Equivalent(25, firstNameType.Modifiers[0]);
        Assert.Equal(1, nameColumn.Constraints.Count);
        nullableConstraint = Assert.IsType<NullableColumnConstraint>(nameColumn.Constraints[0]);
        Assert.False(nullableConstraint.Nullable);
        
        AssertSakilaLastUpdateColumn(createTable, 2);
    }
}
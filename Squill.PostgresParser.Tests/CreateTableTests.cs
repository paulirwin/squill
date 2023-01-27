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
        
        Assert.Equal("id", idColumn.Name);
        Assert.Equal("integer", idColumn.DataType.TypeName);
        Assert.Equal(1, idColumn.Constraints.Count);
        Assert.IsType<PrimaryKeyColumnConstraint>(idColumn.Constraints[0]);
        
        AssertVarcharColumn(createTable, 1, "name", 100, true, false);
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

        AssertSakilaIdColumn(createTable, "actor_id", "actor_actor_id_seq");
        AssertVarcharColumn(createTable, 1, "first_name", 45, true, false);
        AssertVarcharColumn(createTable, 2, "last_name", 45, true, false);

        AssertSakilaLastUpdateColumn(createTable, 3);
    }

    /// <summary>
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
        
        AssertSakilaIdColumn(createTable, "category_id", "category_category_id_seq");
        AssertVarcharColumn(createTable, 1, "name", 25, true, false);
        AssertSakilaLastUpdateColumn(createTable, 2);
    }
    
    /// <summary>
    /// A test for the Sakila sample database `film` table. See license in README.md
    /// </summary>
    [Fact]
    public void Sakila_FilmTableTest()
    {
        var parser = new AntlrPostgresParser();

        // TODO: move to embedded resource
        const string text = """
CREATE TABLE film (
    film_id integer DEFAULT nextval('film_film_id_seq'::regclass) NOT NULL,
    title character varying(255) NOT NULL,
    description text,
    release_year year,
    language_id smallint NOT NULL,
    original_language_id smallint,
    rental_duration smallint DEFAULT 3 NOT NULL,
    rental_rate numeric(4,2) DEFAULT 4.99 NOT NULL,
    length smallint,
    replacement_cost numeric(5,2) DEFAULT 19.99 NOT NULL,
    rating mpaa_rating DEFAULT 'G'::mpaa_rating,
    last_update timestamp without time zone DEFAULT now() NOT NULL,
    special_features text[],
    fulltext tsvector NOT NULL
);
""";

        var root = parser.Parse(text);
        Assert.NotNull(root);
        Assert.Equal(1, root.Statements.Count);

        var createTable = Assert.IsType<CreateTableStatement>(root.Statements[0]);

        Assert.Equal("film", createTable.Name.ToString());
        Assert.Equal(14, createTable.Elements.Count);
        
        AssertSakilaIdColumn(createTable, "film_id", "film_film_id_seq");
        AssertVarcharColumn(createTable, 1, "title", 255, true, false);
        AssertBuiltInDataTypeColumn(createTable, 2, "description", PostgresBuiltInDataType.Text, false);
        AssertUnresolvedDataTypeColumn(createTable, 3, "release_year", "year");
        AssertBuiltInDataTypeColumn(createTable, 4, "language_id", PostgresBuiltInDataType.SmallInt, true, false);
        AssertBuiltInDataTypeColumn(createTable, 5, "original_language_id", PostgresBuiltInDataType.SmallInt, false);
        AssertBuiltInDataTypeColumn(createTable, 6, "rental_duration", PostgresBuiltInDataType.SmallInt, true, false, 3L);
        AssertNumericColumn(createTable, 7, "rental_rate", 4, 2, true, false, 4.99m);
        AssertBuiltInDataTypeColumn(createTable, 8, "length", PostgresBuiltInDataType.SmallInt, false);
        AssertNumericColumn(createTable, 9, "replacement_cost", 5, 2, true, false, 19.99m);
        AssertUnresolvedDataTypeColumn(createTable, 10, "rating", "mpaa_rating"); // TODO: assert default
        AssertSakilaLastUpdateColumn(createTable, 11);
        AssertArrayTypeColumn(createTable, 12, "special_features", PostgresBuiltInDataType.Text, false);
        AssertBuiltInDataTypeColumn(createTable, 13, "fulltext", PostgresBuiltInDataType.TSVector, false);
    }
    
    /// <summary>
    /// A test for the Sakila sample database `customer` table. See license in README.md
    /// </summary>
    [Fact]
    public void Sakila_CustomerTableTest()
    {
        var parser = new AntlrPostgresParser();

        // TODO: move to embedded resource
        const string text = """
CREATE TABLE customer (
    customer_id integer DEFAULT nextval('customer_customer_id_seq'::regclass) NOT NULL,
    store_id smallint NOT NULL,
    first_name character varying(45) NOT NULL,
    last_name character varying(45) NOT NULL,
    email character varying(50),
    address_id smallint NOT NULL,
    activebool boolean DEFAULT true NOT NULL,
    create_date date DEFAULT ('now'::text)::date NOT NULL,
    last_update timestamp without time zone DEFAULT now() NOT NULL,
    active integer
);
""";

        var root = parser.Parse(text);
        Assert.NotNull(root);
        Assert.Equal(1, root.Statements.Count);

        var createTable = Assert.IsType<CreateTableStatement>(root.Statements[0]);

        Assert.Equal("customer", createTable.Name.ToString());
        Assert.Equal(10, createTable.Elements.Count);
        
        AssertSakilaIdColumn(createTable, "customer_id", "customer_customer_id_seq");
        AssertBuiltInDataTypeColumn(createTable, 1, "store_id", PostgresBuiltInDataType.SmallInt, true, false);
        AssertVarcharColumn(createTable, 2, "first_name", 45, true, false);
        AssertVarcharColumn(createTable, 3, "last_name", 45, true, false);
        AssertVarcharColumn(createTable, 4, "email", 50, false);
        AssertBuiltInDataTypeColumn(createTable, 5, "address_id", PostgresBuiltInDataType.SmallInt, true, false);
        AssertBuiltInDataTypeColumn(createTable, 6, "activebool", PostgresBuiltInDataType.Boolean, true, false, true);
        AssertBuiltInDataTypeColumn(createTable, 7, "create_date", PostgresBuiltInDataType.Date, true, false);
        AssertSakilaLastUpdateColumn(createTable, 8);
        AssertBuiltInDataTypeColumn(createTable, 9, "active", PostgresBuiltInDataType.Integer, false);
    }
    
    /// <summary>
    /// A test for the Sakila sample database `language` table. See license in README.md
    /// </summary>
    [Fact]
    public void Sakila_LanguageTableTest()
    {
        var parser = new AntlrPostgresParser();

        // TODO: move to embedded resource
        const string text = """
CREATE TABLE language (
    language_id integer DEFAULT nextval('language_language_id_seq'::regclass) NOT NULL,
    name character(20) NOT NULL,
    last_update timestamp without time zone DEFAULT now() NOT NULL
);
""";

        var root = parser.Parse(text);
        Assert.NotNull(root);
        Assert.Equal(1, root.Statements.Count);

        var createTable = Assert.IsType<CreateTableStatement>(root.Statements[0]);

        Assert.Equal("language", createTable.Name.ToString());
        Assert.Equal(3, createTable.Elements.Count);
        
        AssertSakilaIdColumn(createTable, "language_id", "language_language_id_seq");
        AssertVarcharColumn(createTable, 1, "name", 20, true, false, type: PostgresBuiltInDataType.Char);
        AssertSakilaLastUpdateColumn(createTable, 2);
    }

    private static void AssertSakilaIdColumn(CreateTableStatement createTable, string name, string seqName)
    {
        var idColumn = Assert.IsType<ColumnDefinition>(createTable.Elements[0]);

        Assert.Equal(name, idColumn.Name);
        Assert.Equal("integer", idColumn.DataType.TypeName);
        Assert.Equal(2, idColumn.Constraints.Count);

        var idDefault = Assert.IsType<DefaultColumnConstraint>(idColumn.Constraints[0]);
        var nextvalFunc = Assert.IsType<FunctionApplicationExpression>(idDefault.Expression);
        Assert.Equal("nextval", nextvalFunc.Name);
        Assert.Equal(1, nextvalFunc.Arguments.Count);
        var typecastExpr = Assert.IsType<TypecastExpression>(nextvalFunc.Arguments[0].Expression);
        var stringLiteral = Assert.IsType<LiteralExpression>(typecastExpr.Expression);
        Assert.Equal(seqName, stringLiteral.Value);
        var objIdType = Assert.IsType<ObjectIdentifierTypeName>(typecastExpr.DataType);
        Assert.Equal(PostgresObjectIdentifierTypes.Regclass, objIdType.ObjectIdentifierType);

        var nullableConstraint = Assert.IsType<NullableColumnConstraint>(idColumn.Constraints[1]);
        Assert.False(nullableConstraint.Nullable);
    }

    private static void AssertVarcharColumn(CreateTableStatement createTable, int columnIndex, string name, int length,
        bool assertNullability, bool? nullable = null, PostgresBuiltInDataType type = PostgresBuiltInDataType.Varchar)
    {
        var columnDef = Assert.IsType<ColumnDefinition>(createTable.Elements[columnIndex]);

        Assert.Equal(name, columnDef.Name);
        
        var dataType = Assert.IsType<BuiltInDataType>(columnDef.DataType);
        Assert.Equal(type, dataType.Type);
        Assert.Equal(1, dataType.Modifiers.Count);
        var lengthExpr = Assert.IsType<LiteralExpression>(dataType.Modifiers[0]);
        Assert.Equal((long)length, lengthExpr.Value);
        
        AssertNullability(assertNullability, nullable, columnDef);
    }
    
    private static void AssertNumericColumn(CreateTableStatement createTable, int columnIndex, string name, int precision, int scale, bool assertNullability, bool? nullable = null, object? defaultLiteral = null)
    {
        var columnDef = Assert.IsType<ColumnDefinition>(createTable.Elements[columnIndex]);

        Assert.Equal(name, columnDef.Name);
        
        var dataType = Assert.IsType<BuiltInDataType>(columnDef.DataType);
        Assert.Equal(PostgresBuiltInDataType.Decimal, dataType.Type);
        Assert.Equal(2, dataType.Modifiers.Count);
        
        var precisionExpr = Assert.IsType<LiteralExpression>(dataType.Modifiers[0]);
        Assert.Equal((long)precision, precisionExpr.Value);
        var scaleExpr = Assert.IsType<LiteralExpression>(dataType.Modifiers[1]);
        Assert.Equal((long)scale, scaleExpr.Value);
        
        AssertNullability(assertNullability, nullable, columnDef);
        AssertDefaultLiteral(defaultLiteral, columnDef);
    }

    private static void AssertDefaultLiteral(object? defaultLiteral, ColumnDefinition columnDef)
    {
        if (defaultLiteral != null)
        {
            var defaultConstraint = columnDef.Constraints.OfType<DefaultColumnConstraint>().FirstOrDefault();
            Assert.NotNull(defaultConstraint);

            var defaultLiteralExpr = Assert.IsType<LiteralExpression>(defaultConstraint.Expression);
            Assert.Equal(defaultLiteral, defaultLiteralExpr.Value);
        }
    }

    private static void AssertBuiltInDataTypeColumn(CreateTableStatement createTable, int columnIndex, string name, PostgresBuiltInDataType builtInType, bool assertNullability, bool? nullable = null, object? defaultLiteral = null)
    {
        var columnDef = Assert.IsType<ColumnDefinition>(createTable.Elements[columnIndex]);

        Assert.Equal(name, columnDef.Name);
        
        var dataType = Assert.IsType<BuiltInDataType>(columnDef.DataType);
        Assert.Equal(builtInType, dataType.Type);
        Assert.Equal(0, dataType.Modifiers.Count);

        AssertNullability(assertNullability, nullable, columnDef);
        AssertDefaultLiteral(defaultLiteral, columnDef);
    }
    
    private static void AssertArrayTypeColumn(CreateTableStatement createTable, int columnIndex, string name, PostgresBuiltInDataType builtInType, bool assertNullability, bool? nullable = null, object? defaultLiteral = null)
    {
        var columnDef = Assert.IsType<ColumnDefinition>(createTable.Elements[columnIndex]);

        Assert.Equal(name, columnDef.Name);
        
        var dataType = Assert.IsType<ArrayDataType>(columnDef.DataType);
        var builtInDataType = Assert.IsType<BuiltInDataType>(dataType.ElementType);
        Assert.Equal(builtInType, builtInDataType.Type);
        Assert.Equal(0, dataType.Modifiers.Count);

        AssertNullability(assertNullability, nullable, columnDef);
        AssertDefaultLiteral(defaultLiteral, columnDef);
    }

    private static void AssertNullability(bool assertNullability, bool? nullable, ColumnDefinition columnDef)
    {
        if (assertNullability)
        {
            var nullableConstraint = columnDef.Constraints.OfType<NullableColumnConstraint>().FirstOrDefault();
            Assert.NotNull(nullableConstraint);

            if (nullable == true)
            {
                Assert.True(nullableConstraint.Nullable);
            }
            else if (nullable == false)
            {
                Assert.False(nullableConstraint.Nullable);
            }
        }
    }

    private static void AssertUnresolvedDataTypeColumn(CreateTableStatement createTable, int columnIndex, string name, string typeName)
    {
        var columnDef = Assert.IsType<ColumnDefinition>(createTable.Elements[columnIndex]);

        Assert.Equal(name, columnDef.Name);
        
        var dataType = Assert.IsType<UnresolvedDataType>(columnDef.DataType);
        Assert.Equal(typeName, dataType.TypeName);
        Assert.Equal(0, dataType.Modifiers.Count);
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
}
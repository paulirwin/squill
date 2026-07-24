using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// Generated (computed) column parsing: <c>GENERATED ALWAYS AS (expr) STORED</c> (issue #120).
/// Distinct from <c>GENERATED ... AS IDENTITY</c>, which produces an
/// <see cref="IdentityColumnConstraint"/>.
/// </summary>
public class GeneratedColumnTests
{
    [Fact]
    public void CreateTable_GeneratedStoredColumn()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE Foo
(
    id integer PRIMARY KEY,
    price numeric NOT NULL,
    quantity integer NOT NULL,
    total numeric GENERATED ALWAYS AS (price * quantity) STORED
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        var totalColumn = Assert.IsType<ColumnDefinition>(createTable.Elements[3]);

        Assert.Equal("total", totalColumn.Name.Name);

        var generated = Assert.IsType<GeneratedColumnConstraint>(Assert.Single(totalColumn.Constraints));

        Assert.Equal("\"price\" * \"quantity\"", ExpressionSqlRenderer.Render(generated.Expression));
    }

    [Fact]
    public void CreateTable_GeneratedStoredColumn_WithFunctionCall()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE Person
(
    id integer PRIMARY KEY,
    first_name text NOT NULL,
    last_name text NOT NULL,
    full_name text GENERATED ALWAYS AS (first_name || ' ' || last_name) STORED
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        var fullName = Assert.IsType<ColumnDefinition>(createTable.Elements[3]);

        var generated = Assert.IsType<GeneratedColumnConstraint>(Assert.Single(fullName.Constraints));

        Assert.Contains("first_name", ExpressionSqlRenderer.Render(generated.Expression));
        Assert.Contains("last_name", ExpressionSqlRenderer.Render(generated.Expression));
    }

    [Fact]
    public void CreateTable_GeneratedColumn_WithNotNull()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE Foo
(
    a integer NOT NULL,
    b integer NOT NULL GENERATED ALWAYS AS (a * 2) STORED
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        var b = Assert.IsType<ColumnDefinition>(createTable.Elements[1]);

        Assert.Equal(2, b.Constraints.Count);

        var nullable = Assert.IsType<NullableColumnConstraint>(b.Constraints[0]);
        Assert.False(nullable.Nullable);

        Assert.IsType<GeneratedColumnConstraint>(b.Constraints[1]);
    }

    /// <summary>
    /// GENERATED ... AS IDENTITY must still map to an identity constraint, not a generated
    /// column: the two share the GENERATED keyword but are entirely different features.
    /// </summary>
    [Fact]
    public void CreateTable_GeneratedAsIdentity_IsNotAGeneratedColumn()
    {
        var parser = new AntlrPostgresParser();

        const string text = """
CREATE TABLE Foo
(
    id integer GENERATED ALWAYS AS IDENTITY PRIMARY KEY
);
""";

        var root = parser.Parse(text);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        var id = Assert.IsType<ColumnDefinition>(Assert.Single(createTable.Elements));

        Assert.IsType<IdentityColumnConstraint>(id.Constraints[0]);
    }
}

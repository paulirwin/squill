using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// <c>CREATE TYPE</c> beyond the <c>AS ENUM</c> form (issue #122): composite types
/// (<c>AS (field type, ...)</c>) and range types (<c>AS RANGE (SUBTYPE = ...)</c>). Both
/// previously threw out of <c>VisitDefinestmt</c>.
/// </summary>
public class CreateCompositeAndRangeTypeTests
{
    private static T ParseOne<T>(string text) where T : Statement
    {
        var root = new AntlrPostgresParser().Parse(text);

        return Assert.IsType<T>(Assert.Single(root.Statements));
    }

    // ---- Composite types ----

    [Fact]
    public void CreateCompositeType_Attributes_AreParsedInOrder()
    {
        var statement = ParseOne<CreateCompositeTypeStatement>(
            "CREATE TYPE addr AS (street varchar(60), city text, zip char(5));");

        Assert.Equal("addr", statement.Name.ToString());
        Assert.Equal(["street", "city", "zip"], statement.Attributes.Select(i => i.Name.Name));

        // The declared type of each attribute survives, modifiers included.
        var street = Assert.IsType<BuiltInDataType>(statement.Attributes[0].DataType);
        Assert.Equal(PostgresBuiltInDataType.Varchar, street.Type);
        Assert.Single(street.Modifiers);
    }

    [Fact]
    public void CreateCompositeType_SchemaQualified()
    {
        var statement = ParseOne<CreateCompositeTypeStatement>(
            "CREATE TYPE shipping.addr AS (city text);");

        Assert.Equal("shipping.addr", statement.Name.ToString());
    }

    // An attribute may itself be of a user-defined type or an array.
    [Fact]
    public void CreateCompositeType_UserDefinedAndArrayAttributeTypes()
    {
        var statement = ParseOne<CreateCompositeTypeStatement>(
            "CREATE TYPE parcel AS (destination addr, tags text[]);");

        Assert.Equal(["destination", "tags"], statement.Attributes.Select(i => i.Name.Name));
        Assert.IsType<ArrayDataType>(statement.Attributes[1].DataType);
    }

    // PostgreSQL permits a composite type with no attributes at all.
    [Fact]
    public void CreateCompositeType_NoAttributes()
    {
        var statement = ParseOne<CreateCompositeTypeStatement>("CREATE TYPE empty AS ();");

        Assert.Empty(statement.Attributes);
    }

    // ---- Range types ----

    [Fact]
    public void CreateRangeType_Subtype()
    {
        var statement = ParseOne<CreateRangeTypeStatement>(
            "CREATE TYPE floatrange AS RANGE (SUBTYPE = float8);");

        Assert.Equal("floatrange", statement.Name.ToString());
        Assert.Equal("float8", statement.Subtype.TypeName);
        Assert.Null(statement.SubtypeOperatorClass);
        Assert.Null(statement.Collation);
    }

    // The item names are keywords, so they must parse case-insensitively like the rest of SQL.
    [Fact]
    public void CreateRangeType_ItemNamesAreCaseInsensitive()
    {
        var statement = ParseOne<CreateRangeTypeStatement>(
            "CREATE TYPE r AS RANGE (subtype = text, collation = \"C\");");

        Assert.Equal("text", statement.Subtype.TypeName);
        Assert.Equal("C", statement.Collation);
    }

    [Fact]
    public void CreateRangeType_SubtypeOperatorClass()
    {
        var statement = ParseOne<CreateRangeTypeStatement>(
            "CREATE TYPE r AS RANGE (SUBTYPE = text, SUBTYPE_OPCLASS = text_pattern_ops);");

        Assert.Equal("text_pattern_ops", statement.SubtypeOperatorClass);
    }

    // SUBTYPE is what gives a range type its identity, so a range without one is rejected
    // rather than modeled as something incomplete.
    [Fact]
    public void CreateRangeType_WithoutSubtype_IsRejected()
    {
        var parser = new AntlrPostgresParser();

        var ex = Assert.ThrowsAny<Exception>(
            () => parser.Parse("CREATE TYPE r AS RANGE (COLLATION = \"C\");"));

        Assert.Contains("SUBTYPE", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Still-unsupported CREATE TYPE forms ----

    // A shell type (CREATE TYPE name, no definition) and a base type (CREATE TYPE name (...))
    // remain unmodeled; they must still fail cleanly rather than be silently dropped.
    [Fact]
    public void CreateShellType_IsStillRejected()
    {
        var parser = new AntlrPostgresParser();

        Assert.ThrowsAny<Exception>(() => parser.Parse("CREATE TYPE myshell;"));
    }

    [Fact]
    public void CreateBaseType_IsStillRejected()
    {
        var parser = new AntlrPostgresParser();

        Assert.ThrowsAny<Exception>(
            () => parser.Parse("CREATE TYPE mybase (INPUT = fin, OUTPUT = fout);"));
    }

    // The enum form must keep working — it shares the same grammar alternative set.
    [Fact]
    public void CreateEnumType_StillParses()
    {
        var statement = ParseOne<CreateEnumTypeStatement>(
            "CREATE TYPE mpaa_rating AS ENUM ('G', 'PG');");

        Assert.Equal(["G", "PG"], statement.Labels);
    }
}

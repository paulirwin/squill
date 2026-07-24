using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Model-builder tests for <c>CREATE TYPE ... AS ENUM</c> and <c>CREATE DOMAIN</c> (issue #75):
/// the parsed statements produce the right <c>SqlEnumType</c>/<c>SqlDomain</c> elements, and a
/// table whose column is typed as one still builds.
/// </summary>
public class CreateTypeModelTests
{
    private static Task<Model> BuildModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task EnumType_IsModeledWithOrderedLabels()
    {
        var model = await BuildModelAsync(
            "CREATE TYPE mpaa_rating AS ENUM ('G', 'PG', 'PG-13', 'R', 'NC-17');");

        var enumType = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlEnumType);

        Assert.Equal("mpaa_rating", enumType.Name);
        Assert.Equal("public", PostgresModelFactory.GetSchema(enumType));
        Assert.Equal(["G", "PG", "PG-13", "R", "NC-17"], PostgresModelFactory.GetEnumLabels(enumType));
    }

    [Fact]
    public async Task Domain_IsModeledWithBaseTypeAndCheck()
    {
        var model = await BuildModelAsync(
            "CREATE DOMAIN year AS integer CONSTRAINT year_check CHECK (VALUE >= 1901 AND VALUE <= 2155);");

        var domain = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlDomain);

        Assert.Equal("year", domain.Name);
        Assert.Equal("public", PostgresModelFactory.GetSchema(domain));

        var check = domain.GetProperty<string>(PostgresPropertyNames.CheckExpression);
        Assert.NotNull(check);
        Assert.Contains("1901", check);
        Assert.Contains("2155", check);
    }

    [Fact]
    public async Task Domain_WithNoConstraint_IsModeledWithoutCheck()
    {
        var model = await BuildModelAsync("CREATE DOMAIN us_postal_code AS text;");

        var domain = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlDomain);

        Assert.Equal("us_postal_code", domain.Name);
        Assert.Null(domain.GetProperty<string>(PostgresPropertyNames.CheckExpression));
    }

    [Fact]
    public async Task Table_ColumnTypedAsEnumAndDomain_Builds()
    {
        // The central Pagila shape: a table whose columns are typed as a user-defined enum
        // and a domain. This is the build the issue targets.
        var model = await BuildModelAsync("""
            CREATE TYPE mpaa_rating AS ENUM ('G', 'PG', 'PG-13', 'R', 'NC-17');
            CREATE DOMAIN year AS integer CONSTRAINT year_check CHECK (VALUE >= 1901 AND VALUE <= 2155);
            CREATE TABLE film (
                film_id integer PRIMARY KEY,
                rating mpaa_rating,
                release_year year
            );
            """);

        Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlEnumType);
        Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlDomain);

        var table = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlTable);
        var columns = table.GetRelationship(PostgresRelationshipNames.Columns)!
            .Entries.OfType<Element>().ToList();

        Assert.Equal(["film.film_id", "film.rating", "film.release_year"], columns.Select(c => c.Name));

        // The domain-typed column's type specifier is the domain name (not its base type),
        // matching what the DB-extraction builder resolves from the catalog so a parsed model
        // hash-matches an extracted one (issue #84).
        Assert.Equal("year", TypeReferenceName(columns.Single(c => c.Name == "film.release_year")));
        Assert.Equal("mpaa_rating", TypeReferenceName(columns.Single(c => c.Name == "film.rating")));
    }

    // The canonical type name a column's type specifier references (e.g. "year", "integer").
    private static string TypeReferenceName(Element column)
    {
        var typeSpecifier = column.GetRelationship(PostgresRelationshipNames.TypeSpecifier)!
            .Entries.OfType<Element>().Single();

        return typeSpecifier.GetRelationship(PostgresRelationshipNames.Type)!
            .Entries.OfType<Reference>().Single().Name;
    }

    [Fact]
    public async Task SchemaQualifiedEnum_CarriesItsSchema()
    {
        var model = await BuildModelAsync("""
            CREATE SCHEMA inventory;
            CREATE TYPE inventory.status AS ENUM ('active', 'retired');
            """);

        var enumType = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlEnumType);

        Assert.Equal("status", enumType.Name);
        Assert.Equal("inventory", PostgresModelFactory.GetSchema(enumType));
    }
}

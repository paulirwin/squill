using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Docker-free tests over composite and range types (issue #122): the model shape each
/// produces, how it is scripted, and how a change to one is diffed.
///
/// Composite and range types differ sharply in what PostgreSQL can do to them after
/// creation, and the tests are organized around that: a composite type's attributes can be
/// added and dropped in place, while a range type has no ALTER form at all.
/// </summary>
public class PostgresCompositeAndRangeTypeTests
{
    private static readonly PostgresDatabaseProvider Provider = new("Host=unused");

    private static Task<Model> ParseModelAsync(string sql)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            TestContext.Current.CancellationToken);

    private static async Task<(SchemaComparison Comparison, string Sql)> DiffAsync(
        string sourceSql, string targetSql)
    {
        var source = await ParseModelAsync(sourceSql);
        var target = await ParseModelAsync(targetSql);

        var comparison = SchemaCompare.Compare(Provider, source, target);

        return (comparison, new PostgresScriptGenerator().GenerateScript(comparison));
    }

    private static async Task<Element> SingleElementAsync(string sql, string elementType)
    {
        var model = await ParseModelAsync(sql);

        return Assert.Single(model.Elements, i => i.Type == elementType);
    }

    // ---- Composite type model shape ----

    [Fact]
    public async Task CompositeType_AttributesAreModeledInOrder()
    {
        var element = await SingleElementAsync(
            "CREATE TYPE addr AS (street varchar(60), city text, zip char(5));",
            PostgresElementTypes.SqlCompositeType);

        Assert.Equal("addr", element.Name);
        Assert.Equal("public", PostgresModelFactory.GetSchema(element));

        var attributes = element.GetRelationship(SqlRelationshipNames.Columns)!.Entries
            .OfType<Element>().ToList();

        // An attribute's element name is qualified by its type, the same way a table column's
        // is qualified by its table.
        Assert.Equal(["addr.street", "addr.city", "addr.zip"],
            attributes.Select(i => i.Name?.ToString()));
    }

    // An attribute's type is carried the same way a table column's is, so the modifier is a
    // Length property rather than part of the type name — this is what lets a parsed model
    // hash-match one extracted from the catalog, where format_type() renders it inline.
    [Fact]
    public async Task CompositeType_AttributeTypeCarriesModifierAsAProperty()
    {
        var element = await SingleElementAsync(
            "CREATE TYPE addr AS (street varchar(60));",
            PostgresElementTypes.SqlCompositeType);

        var attribute = Assert.Single(
            element.GetRelationship(SqlRelationshipNames.Columns)!.Entries.OfType<Element>());

        var typeSpecifier = Assert.Single(
            attribute.GetRelationship(SqlRelationshipNames.TypeSpecifier)!.Entries.OfType<Element>());

        Assert.Equal(60, typeSpecifier.GetProperty<int?>(PostgresPropertyNames.Length));
    }

    [Fact]
    public async Task CompositeType_Unchanged_ProducesNoDelta()
    {
        const string sql = "CREATE TYPE addr AS (street varchar(60), city text);";

        var (comparison, _) = await DiffAsync(sql, sql);

        Assert.Empty(comparison.Deltas);
    }

    // ---- Composite type scripting ----

    [Fact]
    public async Task CompositeType_Create_EmitsCreateTypeAs()
    {
        var (_, sql) = await DiffAsync("CREATE TYPE addr AS (street varchar(60), city text);", "");

        Assert.Contains("CREATE TYPE \"addr\" AS (", sql);
        Assert.Contains("\"street\" varchar(60)", sql);
        Assert.Contains("\"city\" text", sql);
    }

    [Fact]
    public async Task CompositeType_SchemaQualifiesNonPublicSchema()
    {
        var (_, sql) = await DiffAsync(
            """
            CREATE SCHEMA shipping;
            CREATE TYPE shipping.addr AS (city text);
            """,
            "CREATE SCHEMA shipping;");

        Assert.Contains("CREATE TYPE \"shipping\".\"addr\" AS (", sql);
    }

    [Fact]
    public async Task CompositeType_Dropped_EmitsDropType()
    {
        var source = await ParseModelAsync("");
        var target = await ParseModelAsync("CREATE TYPE addr AS (city text);");

        var comparison = SchemaCompare.Compare(
            Provider, source, target, new DeployOptions { DropObjectsNotInSource = true });

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("DROP TYPE IF EXISTS \"addr\";", sql);
    }

    // ---- Composite type in-place ALTER ----

    // An added attribute is added in place: DROP TYPE fails whenever a table column is typed
    // as the composite, so a rebuild would break any schema that actually uses it.
    [Fact]
    public async Task CompositeType_AddedAttribute_EmitsAlterTypeAddAttribute()
    {
        var (comparison, sql) = await DiffAsync(
            "CREATE TYPE addr AS (city text, country text);",
            "CREATE TYPE addr AS (city text);");

        Assert.Single(comparison.Deltas);
        Assert.Contains("ALTER TYPE \"addr\" ADD ATTRIBUTE \"country\" text;", sql);
        Assert.DoesNotContain("DROP TYPE", sql);
    }

    [Fact]
    public async Task CompositeType_DroppedAttribute_EmitsAlterTypeDropAttribute()
    {
        var (_, sql) = await DiffAsync(
            "CREATE TYPE addr AS (city text);",
            "CREATE TYPE addr AS (city text, country text);");

        Assert.Contains("ALTER TYPE \"addr\" DROP ATTRIBUTE \"country\";", sql);
        Assert.DoesNotContain("DROP TYPE", sql);
    }

    [Fact]
    public async Task CompositeType_AddedAndDroppedAttributes_EmitsBoth()
    {
        var (_, sql) = await DiffAsync(
            "CREATE TYPE addr AS (city text, postcode text);",
            "CREATE TYPE addr AS (city text, country text);");

        Assert.Contains("ADD ATTRIBUTE \"postcode\"", sql);
        Assert.Contains("DROP ATTRIBUTE \"country\"", sql);
    }

    // PostgreSQL cannot change an attribute's type while any table column uses the composite
    // type — not even with CASCADE. Rather than emit SQL that fails at deploy, the change is
    // reported with a message naming both types.
    [Fact]
    public async Task CompositeType_ChangedAttributeType_FailsWithActionableMessage()
    {
        var source = await ParseModelAsync("CREATE TYPE addr AS (zip varchar(10));");
        var target = await ParseModelAsync("CREATE TYPE addr AS (zip varchar(5));");

        var comparison = SchemaCompare.Compare(Provider, source, target);

        var ex = Assert.Throws<NotSupportedException>(
            () => new PostgresScriptGenerator().GenerateScript(comparison));

        Assert.Contains("addr", ex.Message);
        Assert.Contains("zip", ex.Message);
    }

    // ---- Range types ----

    [Fact]
    public async Task RangeType_SubtypeIsModeled()
    {
        var element = await SingleElementAsync(
            "CREATE TYPE floatrange AS RANGE (SUBTYPE = float8);",
            PostgresElementTypes.SqlRangeType);

        Assert.Equal("floatrange", element.Name);
        Assert.Equal("double precision",
            element.GetProperty<string>(PostgresPropertyNames.Subtype));
    }

    [Fact]
    public async Task RangeType_Create_EmitsCreateTypeAsRange()
    {
        var (_, sql) = await DiffAsync("CREATE TYPE floatrange AS RANGE (SUBTYPE = float8);", "");

        Assert.Contains("CREATE TYPE \"floatrange\" AS RANGE (SUBTYPE = double precision);", sql);
    }

    [Fact]
    public async Task RangeType_OptionalItems_AreScripted()
    {
        var (_, sql) = await DiffAsync(
            "CREATE TYPE r AS RANGE (SUBTYPE = text, SUBTYPE_OPCLASS = text_pattern_ops, COLLATION = \"C\");",
            "");

        Assert.Contains("SUBTYPE = text", sql);
        Assert.Contains("SUBTYPE_OPCLASS = text_pattern_ops", sql);
        Assert.Contains("COLLATION = \"C\"", sql);
    }

    [Fact]
    public async Task RangeType_Unchanged_ProducesNoDelta()
    {
        const string sql = "CREATE TYPE floatrange AS RANGE (SUBTYPE = float8);";

        var (comparison, _) = await DiffAsync(sql, sql);

        Assert.Empty(comparison.Deltas);
    }

    // A range type has no ALTER form in PostgreSQL, and dropping it would fail against any
    // column using it, so a changed subtype is reported rather than scripted.
    [Fact]
    public async Task RangeType_ChangedSubtype_FailsWithActionableMessage()
    {
        var source = await ParseModelAsync("CREATE TYPE r AS RANGE (SUBTYPE = float8);");
        var target = await ParseModelAsync("CREATE TYPE r AS RANGE (SUBTYPE = numeric);");

        var comparison = SchemaCompare.Compare(Provider, source, target);

        var ex = Assert.Throws<NotSupportedException>(
            () => new PostgresScriptGenerator().GenerateScript(comparison));

        Assert.Contains("r", ex.Message);
        Assert.Contains("double precision", ex.Message);
        Assert.Contains("numeric", ex.Message);
    }

    [Fact]
    public async Task RangeType_Dropped_EmitsDropType()
    {
        var source = await ParseModelAsync("");
        var target = await ParseModelAsync("CREATE TYPE r AS RANGE (SUBTYPE = float8);");

        var comparison = SchemaCompare.Compare(
            Provider, source, target, new DeployOptions { DropObjectsNotInSource = true });

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("DROP TYPE IF EXISTS \"r\";", sql);
    }

    // ---- Ordering ----

    // A composite type must be created before a table whose column is typed as it.
    [Fact]
    public async Task CompositeType_IsCreatedBeforeATableThatUsesIt()
    {
        var (_, sql) = await DiffAsync(
            """
            CREATE TYPE addr AS (city text);
            CREATE TABLE customer (id integer NOT NULL, address addr);
            """,
            "");

        var createType = sql.IndexOf("CREATE TYPE", StringComparison.Ordinal);
        var createTable = sql.IndexOf("CREATE TABLE", StringComparison.Ordinal);

        Assert.True(createType >= 0 && createTable > createType,
            $"The composite type must be created before the table:{Environment.NewLine}{sql}");
    }
}

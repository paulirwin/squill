using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// CREATE INDEX clauses that the grammar accepts but the visitor used to drop or reject
/// (issue #160): a per-key-column COLLATE, NULLS NOT DISTINCT, INCLUDE covering columns, a
/// TABLESPACE, and a schema-qualified operator class.
///
/// COLLATE and NULLS NOT DISTINCT are the silent-drop cases — <c>index_elem_options</c> carries
/// a <c>collate_</c> the visitor never read, and <c>indexstmt</c> a <c>nulls_distinct</c> with
/// no property behind it — so an index deployed with the wrong collation, or the opposite
/// uniqueness semantics, and nothing anywhere reported it.
/// </summary>
public class IndexVariantParseTests
{
    private static CreateIndexStatement ParseIndex(string text)
    {
        var root = new AntlrPostgresParser().Parse(text);
        return Assert.IsType<CreateIndexStatement>(Assert.Single(root.Statements));
    }

    [Fact]
    public void IndexElementCollate_IsParsed()
    {
        var index = ParseIndex("""CREATE INDEX ix_people_name ON people (name COLLATE "POSIX");""");

        var element = Assert.Single(index.Elements);

        Assert.NotNull(element.Collation);
        Assert.Equal("POSIX", element.Collation.ToString());
    }

    [Fact]
    public void IndexElementWithoutCollate_HasNoCollation()
    {
        var index = ParseIndex("CREATE INDEX ix_people_name ON people (name);");

        Assert.Null(Assert.Single(index.Elements).Collation);
    }

    /// <summary>
    /// COLLATE precedes the operator class and the ordering keywords in PostgreSQL's synopsis,
    /// so all four must be readable off one element without displacing each other.
    /// </summary>
    [Fact]
    public void IndexElementCollate_CoexistsWithOperatorClassAndOrdering()
    {
        var index = ParseIndex(
            """CREATE INDEX ix ON people (name COLLATE "POSIX" text_pattern_ops DESC NULLS FIRST);""");

        var element = Assert.Single(index.Elements);

        Assert.Equal("POSIX", element.Collation?.ToString());
        Assert.Equal("text_pattern_ops", element.OperatorClass?.ToString());
        Assert.Equal(IndexElementDirection.Desc, element.Direction);
        Assert.Equal(IndexElementNullOrder.NullsFirst, element.NullOrder);
    }

    [Fact]
    public void MultiColumnIndex_CarriesCollationPerColumn()
    {
        var index = ParseIndex(
            """CREATE INDEX ix ON people (name COLLATE "POSIX", last_name, first_name COLLATE "C");""");

        Assert.Equal(3, index.Elements.Count);
        Assert.Equal("POSIX", index.Elements[0].Collation?.ToString());
        Assert.Null(index.Elements[1].Collation);
        Assert.Equal("C", index.Elements[2].Collation?.ToString());
    }

    [Fact]
    public void NullsNotDistinct_IsParsed()
    {
        var index = ParseIndex("CREATE UNIQUE INDEX ix_people_age ON people (age) NULLS NOT DISTINCT;");

        Assert.True(index.Unique);
        Assert.True(index.NullsNotDistinct);
    }

    /// <summary>
    /// NULLS DISTINCT is the explicit spelling of the default, and must not be confused with
    /// NULLS NOT DISTINCT — the grammar's <c>nulls_distinct : NULLS_P NOT? DISTINCT</c> makes
    /// the two differ by one optional token.
    /// </summary>
    [Fact]
    public void NullsDistinct_IsTheDefaultAndNotNullsNotDistinct()
    {
        var index = ParseIndex("CREATE UNIQUE INDEX ix ON people (age) NULLS DISTINCT;");

        Assert.False(index.NullsNotDistinct);
    }

    [Fact]
    public void IndexWithoutNullsClause_IsNotNullsNotDistinct()
    {
        Assert.False(ParseIndex("CREATE UNIQUE INDEX ix ON people (age);").NullsNotDistinct);
    }

    /// <summary>
    /// A per-key NULLS FIRST/LAST ordering and the index-level NULLS NOT DISTINCT both start
    /// with the NULLS keyword but belong to different rules; reading one must not consume the
    /// other.
    /// </summary>
    [Fact]
    public void NullsOrdering_AndNullsNotDistinct_AreIndependent()
    {
        var index = ParseIndex(
            "CREATE UNIQUE INDEX ix ON people (age NULLS FIRST) NULLS NOT DISTINCT;");

        Assert.Equal(IndexElementNullOrder.NullsFirst, Assert.Single(index.Elements).NullOrder);
        Assert.True(index.NullsNotDistinct);
    }

    [Fact]
    public void IncludeColumns_AreParsed()
    {
        var index = ParseIndex(
            "CREATE INDEX ix ON people (name) INCLUDE (first_name, last_name);");

        // The key column list is unaffected by the covering columns.
        var key = Assert.Single(index.Elements);
        Assert.Equal("name", Assert.IsType<ColumnReferenceExpression>(key.Expression).Identifier.Name);

        Assert.Collection(index.IncludeElements,
            c => Assert.Equal("first_name",
                Assert.IsType<ColumnReferenceExpression>(c.Expression).Identifier.Name),
            c => Assert.Equal("last_name",
                Assert.IsType<ColumnReferenceExpression>(c.Expression).Identifier.Name));
    }

    [Fact]
    public void IndexWithoutInclude_HasNoIncludeElements()
    {
        Assert.Empty(ParseIndex("CREATE INDEX ix ON people (name);").IncludeElements);
    }

    [Fact]
    public void SchemaQualifiedOperatorClass_IsParsed()
    {
        var index = ParseIndex(
            "CREATE INDEX ix ON people USING btree (name pg_catalog.text_pattern_ops);");

        var element = Assert.Single(index.Elements);

        // Carried qualified; the model stores the bare name, since that is what the catalog
        // reports back and an opclass name is unique within an access method.
        Assert.Equal("pg_catalog.text_pattern_ops", element.OperatorClass?.ToString());
    }

    [Fact]
    public void BareOperatorClass_IsStillParsed()
    {
        var index = ParseIndex("CREATE INDEX ix ON people USING btree (name text_pattern_ops);");

        Assert.Equal("text_pattern_ops", Assert.Single(index.Elements).OperatorClass?.ToString());
    }

    [Fact]
    public void Tablespace_IsParsed()
    {
        var index = ParseIndex("CREATE INDEX ix ON people (name) TABLESPACE pg_default;");

        Assert.Equal("pg_default", index.TableSpace?.Name);
    }

    [Fact]
    public void IndexWithoutTablespace_HasNoTablespace()
    {
        Assert.Null(ParseIndex("CREATE INDEX ix ON people (name);").TableSpace);
    }

    [Fact]
    public void ExpressionIndex_BareCall_IsParsed()
    {
        var index = ParseIndex("CREATE INDEX ix ON people (lower(name));");

        var element = Assert.Single(index.Elements);
        var call = Assert.IsType<FunctionApplicationExpression>(element.Expression);

        Assert.Equal("lower", call.Name);
    }

    /// <summary>
    /// The parenthesized spelling takes the <c>a_expr</c> alternative of <c>index_elem</c>
    /// rather than <c>func_expr_windowless</c>, so it reaches the visitor by a different route
    /// and is asserted separately.
    /// </summary>
    [Fact]
    public void ExpressionIndex_Parenthesized_IsParsed()
    {
        var index = ParseIndex("CREATE INDEX ix ON people ((lower(name)));");

        var element = Assert.Single(index.Elements);

        Assert.IsType<FunctionApplicationExpression>(element.Expression);
    }
}

using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// Exclusion constraints (issue #212): <c>EXCLUDE USING gist (room WITH =, during WITH &amp;&amp;)</c>.
///
/// Before this, the EXCLUDE alternative of <c>constraintelem</c> parsed but fell through
/// <c>VisitConstraintelem</c> to a terminal <c>NotImplementedException</c>, so a table using one
/// could not be built at all. The grammar rules were all already present; only the visitor was
/// missing.
///
/// The spellings asserted here were measured against a live PostgreSQL server before being
/// written, per CLAUDE.md: the grammar is looser than the engine, so what parses is not
/// evidence of what deploys.
/// </summary>
public class ExcludeConstraintTests
{
    private static ExclusionTableConstraint ParseExclusion(string text)
    {
        var root = new AntlrPostgresParser().Parse(text);
        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        return Assert.Single(createTable.Elements
            .Select(e => e is NamedTableConstraint named ? named.Constraint : e)
            .OfType<ExclusionTableConstraint>());
    }

    private static string ColumnName(ExclusionConstraintElement element)
        => Assert.IsType<ColumnReferenceExpression>(element.Key.Expression).Identifier.Name;

    private static string OperatorName(ExclusionConstraintElement element)
        => element.Operator.Segments[^1].Name;

    [Fact]
    public void Exclude_CanonicalOverlapForm_IsParsed()
    {
        var exclude = ParseExclusion(
            """
            CREATE TABLE booking (
                room integer,
                during tstzrange,
                EXCLUDE USING gist (room WITH =, during WITH &&)
            );
            """);

        Assert.Equal("gist", exclude.AccessMethod?.Name);
        Assert.Equal(2, exclude.Elements.Count);

        Assert.Equal("room", ColumnName(exclude.Elements[0]));
        Assert.Equal("=", OperatorName(exclude.Elements[0]));

        Assert.Equal("during", ColumnName(exclude.Elements[1]));
        Assert.Equal("&&", OperatorName(exclude.Elements[1]));
    }

    [Fact]
    public void Exclude_SingleElement_IsParsed()
    {
        var exclude = ParseExclusion("CREATE TABLE t (a integer, EXCLUDE (a WITH =));");

        var element = Assert.Single(exclude.Elements);
        Assert.Equal("a", ColumnName(element));
        Assert.Equal("=", OperatorName(element));
    }

    // USING is optional in the grammar and in the engine. Measured, an omitted method is
    // reported back as `USING btree`, so the absence is carried here as null and defaulted by
    // the model layer rather than being invented by the parser.
    [Fact]
    public void Exclude_WithoutUsing_HasNoAccessMethod()
    {
        var exclude = ParseExclusion("CREATE TABLE t (a integer, EXCLUDE (a WITH =));");

        Assert.Null(exclude.AccessMethod);
    }

    [Fact]
    public void Exclude_WhereClause_IsParsed()
    {
        var exclude = ParseExclusion(
            """
            CREATE TABLE t (
                room integer,
                during tstzrange,
                active boolean,
                EXCLUDE USING gist (room WITH =, during WITH &&) WHERE (active)
            );
            """);

        Assert.NotNull(exclude.WhereClause);
        Assert.Equal("active",
            Assert.IsType<ColumnReferenceExpression>(exclude.WhereClause).Identifier.Name);
    }

    [Fact]
    public void Exclude_WithoutWhereClause_HasNullPredicate()
    {
        var exclude = ParseExclusion("CREATE TABLE t (a integer, EXCLUDE (a WITH =));");

        Assert.Null(exclude.WhereClause);
    }

    [Fact]
    public void Exclude_NamedConstraint_KeepsItsName()
    {
        var root = new AntlrPostgresParser().Parse(
            """
            CREATE TABLE t (
                room integer,
                during tstzrange,
                CONSTRAINT no_overlap EXCLUDE USING gist (room WITH =, during WITH &&)
            );
            """);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));
        var named = Assert.Single(createTable.Elements.OfType<NamedTableConstraint>());

        Assert.Equal("no_overlap", named.Name.Name);
        Assert.IsType<ExclusionTableConstraint>(named.Constraint);
    }

    [Fact]
    public void Exclude_Deferrable_IsParsed()
    {
        var exclude = ParseExclusion(
            "CREATE TABLE t (a integer, EXCLUDE (a WITH =) DEFERRABLE INITIALLY DEFERRED);");

        Assert.True(exclude.IsDeferrable);
        Assert.True(exclude.IsInitiallyDeferred);
    }

    [Fact]
    public void Exclude_NotDeferrable_IsTheDefault()
    {
        var exclude = ParseExclusion("CREATE TABLE t (a integer, EXCLUDE (a WITH =));");

        Assert.False(exclude.IsDeferrable);
        Assert.False(exclude.IsInitiallyDeferred);
    }

    [Fact]
    public void Exclude_Include_IsParsed()
    {
        var exclude = ParseExclusion(
            "CREATE TABLE t (a integer, b integer, c integer, EXCLUDE (a WITH =) INCLUDE (c));");

        Assert.Equal(["c"], exclude.IncludeColumns.Select(c => c.Name));
    }

    [Fact]
    public void Exclude_StorageParameters_AreParsed()
    {
        var exclude = ParseExclusion(
            "CREATE TABLE t (a integer, EXCLUDE (a WITH =) WITH (fillfactor = 70));");

        var option = Assert.Single(exclude.WithOptions);
        Assert.Equal("fillfactor", option.Name);
        Assert.Equal("70", option.Value);
    }

    [Fact]
    public void Exclude_ExpressionKey_IsParsed()
    {
        var exclude = ParseExclusion("CREATE TABLE t (a text, EXCLUDE (lower(a) WITH =));");

        var element = Assert.Single(exclude.Elements);
        Assert.IsType<FunctionApplicationExpression>(element.Key.Expression);
    }

    [Fact]
    public void Exclude_KeyOrdering_IsParsed()
    {
        var exclude = ParseExclusion(
            "CREATE TABLE t (a integer, b integer, EXCLUDE (a DESC NULLS LAST WITH =, b WITH =));");

        Assert.Equal(IndexElementDirection.Desc, exclude.Elements[0].Key.Direction);
        Assert.Equal(IndexElementNullOrder.NullsLast, exclude.Elements[0].Key.NullOrder);
        Assert.Null(exclude.Elements[1].Key.Direction);
    }

    [Fact]
    public void Exclude_KeyOperatorClass_IsParsed()
    {
        var exclude = ParseExclusion(
            "CREATE TABLE t (a integer, EXCLUDE USING btree (a int4_ops WITH =));");

        var element = Assert.Single(exclude.Elements);
        Assert.Equal("int4_ops", element.Key.OperatorClass?.Segments[^1].Name);
    }

    // The explicit OPERATOR(schema.op) spelling. Measured, PostgreSQL reports an operator
    // resolved in pg_catalog unqualified, so `OPERATOR(pg_catalog.=)` comes back as plain `=`
    // -- the source spelling does not survive, which is why the model canonicalizes rather
    // than storing what was written.
    [Fact]
    public void Exclude_ExplicitOperatorSpelling_IsParsed()
    {
        var exclude = ParseExclusion(
            "CREATE TABLE t (a integer, EXCLUDE (a WITH OPERATOR(pg_catalog.=)));");

        var element = Assert.Single(exclude.Elements);
        Assert.Equal(["pg_catalog", "="], element.Operator.Segments.Select(s => s.Name));
    }

    [Fact]
    public void Exclude_SchemaQualifiedOperator_KeepsItsSchema()
    {
        var exclude = ParseExclusion(
            "CREATE TABLE t (a integer, EXCLUDE (a WITH OPERATOR(myops.===)));");

        var element = Assert.Single(exclude.Elements);
        Assert.Equal(["myops", "==="], element.Operator.Segments.Select(s => s.Name));
    }

    [Fact]
    public void Exclude_AlongsideOtherConstraints_IsParsedTogetherWithThem()
    {
        var root = new AntlrPostgresParser().Parse(
            """
            CREATE TABLE booking (
                id integer PRIMARY KEY,
                room integer NOT NULL,
                during tstzrange NOT NULL,
                CHECK (room > 0),
                EXCLUDE USING gist (room WITH =, during WITH &&)
            );
            """);

        var createTable = Assert.IsType<CreateTableStatement>(Assert.Single(root.Statements));

        Assert.Single(createTable.Elements
            .Select(e => e is NamedTableConstraint named ? named.Constraint : e)
            .OfType<ExclusionTableConstraint>());
        Assert.Single(createTable.Elements
            .Select(e => e is NamedTableConstraint named ? named.Constraint : e)
            .OfType<CheckTableConstraint>());
    }
}

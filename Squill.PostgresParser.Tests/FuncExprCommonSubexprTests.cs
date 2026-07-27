using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser.Tests;

/// <summary>
/// Issue #140: the <c>func_expr_common_subexpr</c> grammar rule — the alternative of
/// <c>func_expr</c> that is not a plain <c>func_application</c>. It covers the niladic
/// keywords (<c>CURRENT_TIMESTAMP</c>, <c>CURRENT_USER</c>, …), <c>CAST</c>, the
/// keyword-separated string functions (<c>SUBSTRING ... FROM ... FOR</c>, <c>TRIM</c>,
/// <c>POSITION ... IN</c>, <c>OVERLAY ... PLACING</c>), <c>EXTRACT ... FROM</c>, and the
/// comma-list functions (<c>COALESCE</c>, <c>NULLIF</c>, <c>GREATEST</c>, <c>LEAST</c>).
///
/// These appear in every expression position — column <c>DEFAULT</c>, <c>CHECK</c>
/// predicate, index expression, view body — so before this each one failed the build.
/// </summary>
public class FuncExprCommonSubexprTests
{
    // Parses a standalone expression by wrapping it in a partial index's WHERE clause,
    // which is an a_expr position, and pulling the parsed expression back out.
    private static Expression ParseExpression(string expression)
    {
        var parser = new AntlrPostgresParser();
        var root = parser.Parse($"CREATE INDEX i ON t (c) WHERE {expression};");
        var createIndex = Assert.IsType<CreateIndexStatement>(root.Statements[0]);
        Assert.NotNull(createIndex.WhereClause);
        return createIndex.WhereClause;
    }

    private static Expression ParseDefault(string expression)
    {
        var parser = new AntlrPostgresParser();
        var root = parser.Parse($"CREATE TABLE t (c integer DEFAULT {expression});");
        var createTable = Assert.IsType<CreateTableStatement>(root.Statements[0]);
        var column = Assert.IsType<ColumnDefinition>(createTable.Elements[0]);
        var @default = Assert.Single(column.Constraints.OfType<DefaultColumnConstraint>());
        return @default.Expression;
    }

    [Theory]
    [InlineData("CURRENT_DATE", "CURRENT_DATE")]
    [InlineData("current_date", "CURRENT_DATE")]
    [InlineData("CURRENT_TIME", "CURRENT_TIME")]
    [InlineData("CURRENT_TIMESTAMP", "CURRENT_TIMESTAMP")]
    [InlineData("LOCALTIME", "LOCALTIME")]
    [InlineData("LOCALTIMESTAMP", "LOCALTIMESTAMP")]
    [InlineData("CURRENT_ROLE", "CURRENT_ROLE")]
    [InlineData("CURRENT_USER", "CURRENT_USER")]
    [InlineData("SESSION_USER", "SESSION_USER")]
    [InlineData("USER", "USER")]
    [InlineData("CURRENT_CATALOG", "CURRENT_CATALOG")]
    [InlineData("CURRENT_SCHEMA", "CURRENT_SCHEMA")]
    public void NiladicKeyword_ParsesToKeywordExpression(string sql, string expectedKeyword)
    {
        var keyword = Assert.IsType<KeywordExpression>(ParseDefault(sql));
        Assert.Equal(expectedKeyword, keyword.Keyword);
        Assert.Null(keyword.Precision);
    }

    /// <summary>
    /// The time keywords accept an optional fractional-seconds precision, which is part of
    /// the expression and must be carried, not dropped.
    /// </summary>
    [Theory]
    [InlineData("CURRENT_TIME(3)", "CURRENT_TIME", 3)]
    [InlineData("CURRENT_TIMESTAMP(6)", "CURRENT_TIMESTAMP", 6)]
    [InlineData("LOCALTIME(0)", "LOCALTIME", 0)]
    [InlineData("LOCALTIMESTAMP(2)", "LOCALTIMESTAMP", 2)]
    public void NiladicKeyword_WithPrecision_CarriesPrecision(string sql, string expectedKeyword,
        int expectedPrecision)
    {
        var keyword = Assert.IsType<KeywordExpression>(ParseDefault(sql));
        Assert.Equal(expectedKeyword, keyword.Keyword);
        Assert.Equal(expectedPrecision, keyword.Precision);
    }

    [Fact]
    public void Cast_ParsesToCastExpression()
    {
        var cast = Assert.IsType<CastExpression>(ParseDefault("CAST('5' AS integer)"));
        Assert.Equal("integer", cast.DataType.TypeName);
        var literal = Assert.IsType<LiteralExpression>(cast.Expression);
        Assert.Equal("5", literal.Value);
    }

    [Fact]
    public void Treat_ParsesToCastExpression_MarkedAsTreat()
    {
        var cast = Assert.IsType<CastExpression>(ParseDefault("TREAT(c AS integer)"));
        Assert.True(cast.IsTreat);
        Assert.Equal("integer", cast.DataType.TypeName);
    }

    [Theory]
    [InlineData("COALESCE(a, b)", "COALESCE", 2)]
    [InlineData("COALESCE(a, b, c)", "COALESCE", 3)]
    [InlineData("NULLIF(a, b)", "NULLIF", 2)]
    [InlineData("GREATEST(a, b, c)", "GREATEST", 3)]
    [InlineData("LEAST(a, b)", "LEAST", 2)]
    public void CommaListFunction_ParsesToFunctionApplication(string sql, string expectedName,
        int expectedArgs)
    {
        var func = Assert.IsType<FunctionApplicationExpression>(ParseDefault(sql));
        Assert.Equal(expectedName, func.Name);
        Assert.Equal(expectedArgs, func.Arguments.Count);
    }

    [Fact]
    public void Extract_ParsesToExtractExpression()
    {
        var extract = Assert.IsType<ExtractExpression>(ParseDefault("EXTRACT(YEAR FROM c)"));
        Assert.Equal("YEAR", extract.Field);
        Assert.IsType<ColumnReferenceExpression>(extract.Source);
    }

    [Fact]
    public void Substring_FromFor_ParsesToSubstringExpression()
    {
        var substring = Assert.IsType<SubstringExpression>(ParseDefault("SUBSTRING(c FROM 1 FOR 3)"));
        Assert.NotNull(substring.From);
        Assert.NotNull(substring.For);
        Assert.Null(substring.Similar);
    }

    [Fact]
    public void Substring_CommaForm_ParsesToFunctionApplication()
    {
        // SUBSTRING(a, b, c) is the plain comma form and models as an ordinary call.
        var func = Assert.IsType<FunctionApplicationExpression>(ParseDefault("SUBSTRING(c, 1, 3)"));
        Assert.Equal("SUBSTRING", func.Name);
        Assert.Equal(3, func.Arguments.Count);
    }

    [Theory]
    [InlineData("TRIM(c)", TrimSide.Both)]
    [InlineData("TRIM(BOTH c)", TrimSide.Both)]
    [InlineData("TRIM(LEADING c)", TrimSide.Leading)]
    [InlineData("TRIM(TRAILING c)", TrimSide.Trailing)]
    public void Trim_ParsesToTrimExpression(string sql, TrimSide expectedSide)
    {
        var trim = Assert.IsType<TrimExpression>(ParseDefault(sql));
        Assert.Equal(expectedSide, trim.Side);
        Assert.Null(trim.Characters);
        Assert.Single(trim.Sources);
    }

    [Fact]
    public void Trim_WithCharacters_CarriesCharactersSeparately()
    {
        var trim = Assert.IsType<TrimExpression>(ParseDefault("TRIM(BOTH 'x' FROM c)"));
        Assert.Equal(TrimSide.Both, trim.Side);
        Assert.NotNull(trim.Characters);
        Assert.Single(trim.Sources);
    }

    [Fact]
    public void Position_ParsesToPositionExpression()
    {
        var position = Assert.IsType<PositionExpression>(ParseDefault("POSITION('a' IN c)"));
        Assert.IsType<LiteralExpression>(position.Substring);
        Assert.IsType<ColumnReferenceExpression>(position.Source);
    }

    [Fact]
    public void Overlay_ParsesToOverlayExpression()
    {
        var overlay = Assert.IsType<OverlayExpression>(
            ParseDefault("OVERLAY(c PLACING 'x' FROM 2 FOR 3)"));
        Assert.NotNull(overlay.From);
        Assert.NotNull(overlay.For);
    }

    [Fact]
    public void Overlay_WithoutFor_LeavesForNull()
    {
        var overlay = Assert.IsType<OverlayExpression>(
            ParseDefault("OVERLAY(c PLACING 'x' FROM 2)"));
        Assert.Null(overlay.For);
    }

    [Fact]
    public void Normalize_ParsesToNormalizeExpression()
    {
        var normalize = Assert.IsType<NormalizeExpression>(ParseDefault("NORMALIZE(c, NFKC)"));
        Assert.Equal("NFKC", normalize.Form);

        var noForm = Assert.IsType<NormalizeExpression>(ParseDefault("NORMALIZE(c)"));
        Assert.Null(noForm.Form);
    }

    [Fact]
    public void CollationFor_ParsesToCollationForExpression()
    {
        var collation = Assert.IsType<CollationForExpression>(ParseDefault("COLLATION FOR (c)"));
        Assert.IsType<ColumnReferenceExpression>(collation.Expression);
    }

    /// <summary>
    /// The rule appears in every expression position, not just a column DEFAULT — a CHECK
    /// predicate is the other one that matters most in practice.
    /// </summary>
    [Fact]
    public void CommonSubexpr_WorksInCheckConstraint()
    {
        var parser = new AntlrPostgresParser();
        var root = parser.Parse(
            "CREATE TABLE t (c timestamp, CHECK (c <= CURRENT_TIMESTAMP));");
        var createTable = Assert.IsType<CreateTableStatement>(root.Statements[0]);
        var check = Assert.Single(createTable.Elements.OfType<CheckTableConstraint>());
        var binary = Assert.IsType<BinaryExpression>(check.Expression);
        Assert.IsType<KeywordExpression>(binary.Right);
    }

    [Fact]
    public void CommonSubexpr_WorksInIndexPredicate()
    {
        var expression = ParseExpression("created_at < CURRENT_DATE");
        var binary = Assert.IsType<BinaryExpression>(expression);
        Assert.IsType<KeywordExpression>(binary.Right);
    }
}

using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Model- and script-level tests for the index-key facets that used to be discarded between the
/// parser and the deployed DDL (issue #161): a <b>prefix length</b> (<c>Brand(20)</c>) and an
/// <b>expression key</b> (<c>(a + b)</c>).
///
/// <para>
/// The integration tests in <c>Squill.IntegrationTests</c> prove the deployed shape against a
/// real server; these prove the two steps in between — that the facet reaches the model, and
/// that it survives into the generated SQL — without needing Docker.
/// </para>
///
/// <para>
/// Both facets follow the omit-when-default convention: <c>information_schema.STATISTICS</c>
/// reports <c>SUB_PART</c> NULL for a whole-column key, so a key with no declared prefix must
/// carry no property at all, or every ordinary index would re-diff against its own database.
/// </para>
/// </summary>
public class MariaDbIndexKeyFidelityTests
{
    private static async Task<Model> BuildModelAsync(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return (await new ParserWorkspaceModelBuilder(
                workspace, new AntlrMariaDbParser(), new MariaDb12DatabaseSchemaProvider())
            .ExtractModelAsync(TestContext.Current.CancellationToken)).Model;
    }

    private static async Task<string> ScriptAsync(string sql)
    {
        var model = await BuildModelAsync(sql);
        var provider = new MariaDbDatabaseProvider("Server=unused");

        return new MariaDbScriptGenerator().GenerateScript(
            SchemaCompare.Compare(provider, model, new Model()));
    }

    private static IReadOnlyList<Element> KeysOf(Model model, string elementType, string name)
    {
        var element = Assert.Single(
            model.Elements, e => e.Type == elementType && e.Name?.ToString() == name);

        return RelationshipHelpers.GetColumnSpecifications(element).ToList();
    }

    // ---- Prefix lengths in the model ----

    [Fact]
    public async Task Index_PrefixLength_IsModeledOnTheDeclaredKeyOnly()
    {
        var model = await BuildModelAsync("""
            CREATE TABLE IceCreams
            (
                IceCreamId int NOT NULL,
                Brand      varchar(128) NOT NULL,
                Name       varchar(128) NOT NULL,
                PRIMARY KEY (IceCreamId)
            );
            CREATE INDEX IX_IceCreams_Brand ON IceCreams (Name, Brand(20));
            """);

        var keys = KeysOf(model, MariaDbElementTypes.SqlIndex, "IX_IceCreams_Brand");

        Assert.Equal(2, keys.Count);

        // Name was declared in full: no prefix property at all, matching SUB_PART NULL.
        Assert.Null(keys[0].GetProperty<int?>(MariaDbPropertyNames.PrefixLength));
        Assert.Equal(20, keys[1].GetProperty<int?>(MariaDbPropertyNames.PrefixLength));
    }

    [Fact]
    public async Task PrimaryKey_PrefixLength_IsModeled()
    {
        var model = await BuildModelAsync("""
            CREATE TABLE IceCreams
            (
                Brand varchar(64) NOT NULL,
                Name  varchar(64) NOT NULL,
                PRIMARY KEY (Name, Brand(20))
            );
            """);

        var keys = KeysOf(model, MariaDbElementTypes.SqlPrimaryKeyConstraint, "PRIMARY");

        Assert.Equal(2, keys.Count);
        Assert.Null(keys[0].GetProperty<int?>(MariaDbPropertyNames.PrefixLength));
        Assert.Equal(20, keys[1].GetProperty<int?>(MariaDbPropertyNames.PrefixLength));
    }

    [Fact]
    public async Task UniqueKey_PrefixLength_IsModeled()
    {
        var model = await BuildModelAsync("""
            CREATE TABLE t
            (
                a varchar(64) NOT NULL,
                UNIQUE KEY uq_a (a(15))
            );
            """);

        var key = Assert.Single(KeysOf(model, MariaDbElementTypes.SqlIndex, "uq_a"));

        Assert.Equal(15, key.GetProperty<int?>(MariaDbPropertyNames.PrefixLength));
    }

    // ---- Prefix lengths in the generated SQL ----

    [Fact]
    public async Task GenerateScript_IndexPrefixLength_IsEmitted()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE IceCreams
            (
                IceCreamId int NOT NULL,
                Brand      varchar(128) NOT NULL,
                Name       varchar(128) NOT NULL,
                PRIMARY KEY (IceCreamId)
            );
            CREATE INDEX IX_IceCreams_Brand ON IceCreams (Name, Brand(20));
            """);

        Assert.Contains("(`Name`, `Brand`(20))", sql);
    }

    [Fact]
    public async Task GenerateScript_PrimaryKeyPrefixLength_IsEmitted()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE IceCreams
            (
                Brand varchar(64) NOT NULL,
                Name  varchar(64) NOT NULL,
                PRIMARY KEY (Name, Brand(20))
            );
            """);

        Assert.Contains("PRIMARY KEY (`Name`, `Brand`(20))", sql);
    }

    [Fact]
    public async Task GenerateScript_UniqueKeyPrefixLength_IsEmitted()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE t
            (
                a varchar(64) NOT NULL,
                UNIQUE KEY uq_a (a(15))
            );
            """);

        Assert.Contains("UNIQUE KEY `uq_a` (`a`(15))", sql);
    }

    // Indexing a TEXT column is legal only WITH a prefix on MySQL, which rejects the
    // prefix-less form outright with error 1170. The prefix reaching the DDL is therefore what
    // makes this schema deployable at all, not a refinement of it.
    [Fact]
    public async Task GenerateScript_PrefixLengthOnTextColumn_IsEmitted()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE articles
            (
                article_id int NOT NULL PRIMARY KEY,
                Body       text NOT NULL
            );
            CREATE INDEX ix_articles_body ON articles (Body(100));
            """);

        Assert.Contains("`Body`(100)", sql);
    }

    // A key with no declared prefix must emit none — a bare `(20)`-less column — so the DDL
    // matches what the catalog will report back.
    [Fact]
    public async Task GenerateScript_WithoutPrefixLength_EmitsNoLength()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE t
            (
                a varchar(64) NOT NULL
            );
            CREATE INDEX ix_a ON t (a);
            """);

        Assert.Contains("(`a`)", sql);
    }

    [Fact]
    public async Task GenerateScript_PrefixLengthWithDescendingKey_EmitsBoth()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE t
            (
                a varchar(64) NOT NULL
            );
            CREATE INDEX ix_a ON t (a(10) DESC);
            """);

        Assert.Contains("`a`(10) DESC", sql);
    }

    // ---- Expression keys ----

    [Fact]
    public async Task Index_ExpressionKey_IsModeledAsAKeyRatherThanDropped()
    {
        var model = await BuildModelAsync("""
            CREATE TABLE totals
            (
                total_id int NOT NULL PRIMARY KEY,
                a        int NOT NULL,
                b        int NOT NULL,
                c        int NOT NULL
            );
            CREATE INDEX ix_totals_sum ON totals ((a + b), c);
            """);

        var keys = KeysOf(model, MariaDbElementTypes.SqlIndex, "ix_totals_sum");

        // The declared index has TWO keys. Previously the expression key was skipped outright,
        // so an index with fewer keys than declared deployed with no warning.
        Assert.Equal(2, keys.Count);

        var keyExpression = keys[0].GetProperty<string>(MariaDbPropertyNames.KeyExpression);
        Assert.NotNull(keyExpression);
        Assert.Contains("a", keyExpression);
        Assert.Contains("b", keyExpression);

        // An expression key names no column, so it carries no Column relationship.
        Assert.Null(keys[0].GetRelationship(MariaDbRelationshipNames.Column));

        // The plain key beside it is unaffected.
        Assert.Null(keys[1].GetProperty<string>(MariaDbPropertyNames.KeyExpression));
        Assert.NotNull(keys[1].GetRelationship(MariaDbRelationshipNames.Column));
    }

    /// <summary>
    /// The declared spelling and the spelling MySQL stores must reduce to the same canonical
    /// form, or a functional index would re-diff on every deploy.
    ///
    /// <para>
    /// Measured on <c>mysql:latest</c>: a key declared <c>(a + b)</c> comes back from
    /// <c>information_schema.STATISTICS.EXPRESSION</c> as <c>(`a` + `b`)</c> — backtick-quoted
    /// and wrapped. Both differences are ones <see cref="ExpressionNormalizer"/> already
    /// reconciles for CHECK predicates (issue #156), which is why the key expression is carried
    /// as the same raw/canonical pair rather than as one string.
    /// </para>
    /// </summary>
    [Fact]
    public void ExpressionKey_DeclaredAndStoredSpellings_ShareOneCanonicalForm()
    {
        Assert.True(ExpressionNormalizer.TryNormalize("a + b", out var declared));
        Assert.True(ExpressionNormalizer.TryNormalize("(`a` + `b`)", out var stored));

        Assert.Equal(declared, stored);
    }

    [Fact]
    public async Task GenerateScript_ExpressionKey_IsEmittedParenthesized()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE totals
            (
                total_id int NOT NULL PRIMARY KEY,
                a        int NOT NULL,
                b        int NOT NULL,
                c        int NOT NULL
            );
            CREATE INDEX ix_totals_sum ON totals ((a + b), c);
            """);

        // Both engines require a functional key to be parenthesized, and both declared keys
        // must appear.
        Assert.Contains("(a + b)", sql);
        Assert.Contains("`c`", sql);

        // Exactly one pair of parentheses: a key read back from STATISTICS.EXPRESSION already
        // carries them, so wrapping unconditionally would emit `((a + b))` when scripting from
        // an extracted model.
        Assert.DoesNotContain("((a + b))", sql);
    }

    /// <summary>
    /// An expression that is already wrapped must not be wrapped again, but one whose outer
    /// parentheses close mid-expression — <c>(a) + (b)</c> — genuinely needs them, so the check
    /// has to walk depth rather than look at the first and last characters.
    /// </summary>
    [Theory]
    [InlineData("(a + b)", "(a + b)")]
    [InlineData("a + b", "(a + b)")]
    [InlineData("(a) + (b)", "((a) + (b))")]
    public async Task GenerateScript_ExpressionKey_IsWrappedExactlyOnce(
        string declared, string expected)
    {
        var sql = await ScriptAsync($"""
            CREATE TABLE t
            (
                id int NOT NULL PRIMARY KEY,
                a  int NOT NULL,
                b  int NOT NULL
            );
            CREATE INDEX ix ON t ({declared});
            """);

        Assert.Contains(expected, sql);
    }
}

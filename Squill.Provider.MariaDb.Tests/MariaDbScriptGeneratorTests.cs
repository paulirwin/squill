using Squill.Core;
using Squill.MariaDbParser;

namespace Squill.Provider.MariaDb.Tests;

/// <summary>
/// Fast, Docker-free tests over the pure model-to-SQL generation. Input models are built
/// with the parser-based model builder (no database required) and diffed against an empty
/// target so every element becomes a CreateDelta.
/// </summary>
public class MariaDbScriptGeneratorTests
{
    private static async Task<SchemaComparison> CompareToEmptyAsync(string sql)
    {
        var parser = new AntlrMariaDbParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        var model = (await new ParserWorkspaceModelBuilder(workspace, parser, new MariaDb12DatabaseSchemaProvider()).ExtractModelAsync()).Model;

        var provider = new MariaDbDatabaseProvider("Server=unused");
        return SchemaCompare.Compare(provider, model, new Model());
    }

    private static async Task<string> ScriptAsync(string sql)
        => new MariaDbScriptGenerator().GenerateScript(await CompareToEmptyAsync(sql));

    [Fact]
    public async Task GenerateScript_CreateTable_UsesBacktickQuotingAndNullability()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE film
            (
                film_id int NOT NULL PRIMARY KEY,
                title   varchar(255) NOT NULL
            );
            """);

        Assert.Contains("CREATE TABLE `film`", sql);
        Assert.Contains("`title` varchar(255) NOT NULL", sql);
        Assert.Contains("PRIMARY KEY (`film_id`)", sql);
    }

    [Fact]
    public async Task GenerateScript_AutoIncrementColumn_EmitsAutoIncrement()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE film
            (
                film_id int NOT NULL AUTO_INCREMENT PRIMARY KEY
            );
            """);

        Assert.Contains("`film_id` int NOT NULL AUTO_INCREMENT", sql);
    }

    [Fact]
    public async Task GenerateScript_Default_EmitsDefaultClause()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE film
            (
                status varchar(20) NOT NULL DEFAULT 'active'
            );
            """);

        Assert.Contains("`status` varchar(20) NOT NULL DEFAULT 'active'", sql);
    }

    /// <summary>
    /// A fractional-seconds <c>CURRENT_TIMESTAMP</c> default and its <c>ON UPDATE</c> clause
    /// (issue #144). The column's own precision has to be emitted alongside them: MySQL rejects
    /// an <c>ON UPDATE</c> precision that disagrees with the column's, so scripting a bare
    /// <c>datetime</c> here would produce DDL the engine refuses.
    /// </summary>
    [Fact]
    public async Task GenerateScript_FractionalPrecisionTimestamp_EmitsPrecisionEverywhere()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE film
            (
                updated datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3)
                            ON UPDATE CURRENT_TIMESTAMP(3)
            );
            """);

        Assert.Contains(
            "`updated` datetime(3) NOT NULL DEFAULT CURRENT_TIMESTAMP(3) "
            + "ON UPDATE CURRENT_TIMESTAMP(3)",
            sql);
    }

    /// <summary>
    /// The whole-second form takes no parentheses in either position, and the column type keeps
    /// none either — matching how both engines report it.
    /// </summary>
    [Fact]
    public async Task GenerateScript_WholeSecondTimestamp_EmitsNoPrecision()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE film
            (
                updated timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP
            );
            """);

        Assert.Contains(
            "`updated` timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP",
            sql);
    }

    [Fact]
    public async Task GenerateScript_Decimal_EmitsPrecisionAndScale()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE payment
            (
                amount decimal(10, 2) NOT NULL
            );
            """);

        Assert.Contains("`amount` decimal(10, 2) NOT NULL", sql);
    }

    [Fact]
    public async Task GenerateScript_ForeignKey_EmitsConstraintClause()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE customer
            (
                id int NOT NULL PRIMARY KEY
            );
            CREATE TABLE orders
            (
                id int NOT NULL PRIMARY KEY,
                customer_id int NOT NULL,
                CONSTRAINT fk_customer FOREIGN KEY (customer_id) REFERENCES customer (id) ON DELETE CASCADE
            );
            """);

        Assert.Contains(
            "CONSTRAINT `fk_customer` FOREIGN KEY (`customer_id`) REFERENCES `customer` (`id`) ON DELETE CASCADE",
            sql);
    }

    [Fact]
    public async Task GenerateScript_UniqueColumn_EmitsUniqueKeyClause()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE account
            (
                id    int NOT NULL PRIMARY KEY,
                email varchar(255) NOT NULL UNIQUE
            );
            """);

        Assert.Contains("UNIQUE KEY `email` (`email`)", sql);
    }

    [Fact]
    public async Task GenerateScript_StandaloneIndex_EmitsCreateIndex()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE film
            (
                film_id int NOT NULL PRIMARY KEY,
                title   varchar(255) NOT NULL
            );
            CREATE INDEX ix_film_title ON film (title);
            """);

        Assert.Contains("CREATE INDEX `ix_film_title` ON `film` (`title`);", sql);
    }

    /// <summary>
    /// A FULLTEXT / SPATIAL index is written with its kind as a leading keyword (issue #146).
    /// The kind must never be emitted as a <c>USING</c> access method: both engines reject
    /// <c>USING FULLTEXT</c> as a syntax error, so getting this wrong produces DDL that fails at
    /// deploy time rather than merely diffing oddly.
    /// </summary>
    [Theory]
    [InlineData("FULLTEXT KEY idx_title (title)", "CREATE FULLTEXT INDEX `idx_title` ON `film` (`title`);")]
    [InlineData("SPATIAL KEY idx_geo (geo)", "CREATE SPATIAL INDEX `idx_geo` ON `film` (`geo`);")]
    public async Task GenerateScript_SpecialIndex_EmitsKindAsPrefixKeyword(
        string indexClause, string expected)
    {
        var sql = await ScriptAsync($"""
            CREATE TABLE film
            (
                film_id int NOT NULL PRIMARY KEY,
                title   varchar(255) NOT NULL,
                geo     geometry NOT NULL,
                {indexClause}
            );
            """);

        Assert.Contains(expected, sql);
        Assert.DoesNotContain("USING FULLTEXT", sql);
        Assert.DoesNotContain("USING SPATIAL", sql);
    }

    [Fact]
    public async Task GenerateScript_EnumColumn_PreservesValueList()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE film
            (
                film_id int unsigned NOT NULL AUTO_INCREMENT PRIMARY KEY,
                rating  enum('G','PG','PG-13','R','NC-17') DEFAULT 'G'
            );
            """);

        Assert.Contains("`rating` enum('G','PG','PG-13','R','NC-17')", sql);
        Assert.DoesNotContain("enum NULL", sql);
    }

    [Fact]
    public async Task GenerateScript_SetColumn_PreservesValueList()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE film
            (
                special_features set('Trailers','Commentaries','Deleted Scenes','Behind the Scenes')
            );
            """);

        Assert.Contains(
            "`special_features` set('Trailers','Commentaries','Deleted Scenes','Behind the Scenes')",
            sql);
        Assert.DoesNotContain("set NULL", sql);
    }

    [Fact]
    public async Task GenerateScript_SeparatesStepsWithBlankLine()
    {
        var sql = await ScriptAsync("""
            CREATE TABLE a (id int NOT NULL PRIMARY KEY);
            CREATE TABLE b (id int NOT NULL PRIMARY KEY);
            """);

        var newline = Environment.NewLine;
        Assert.Contains($"{newline}{newline}CREATE ", sql);
    }

    [Fact]
    public void RebuildAsideName_ShortName_UsesSuffixVerbatim()
    {
        Assert.Equal("film__squill_rebuild_old", MariaDbScriptGenerator.RebuildAsideName("film"));
    }

    [Fact]
    public void RebuildAsideName_LongNames_StayWithinLimitAndStayDistinct()
    {
        var a = MariaDbScriptGenerator.RebuildAsideName(new string('a', 60));
        var b = MariaDbScriptGenerator.RebuildAsideName(new string('a', 59) + "b");

        // MariaDB caps identifiers at 64 characters.
        Assert.True(a.Length <= 64);
        Assert.True(b.Length <= 64);
        // Two distinct long names that share a truncated prefix must not collide.
        Assert.NotEqual(a, b);
    }
}

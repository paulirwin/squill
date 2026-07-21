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

        var model = (await new ParserWorkspaceModelBuilder(workspace, parser).ExtractModelAsync()).Model;

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
}

using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Fast, Docker-free tests over the pure model-to-SQL generation. Input models are
/// built with the parser-based model builder (no database required) and diffed
/// against an empty target so every element becomes a CreateDelta.
/// </summary>
public class PostgresScriptGeneratorTests
{
    private static async Task<SchemaComparison> CompareToEmptyAsync(string sql)
    {
        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        var model = await new ParserWorkspaceModelBuilder(workspace, parser).ExtractModelAsync();

        var provider = new PostgresDatabaseProvider("Host=unused");
        return SchemaCompare.Compare(provider, model, new Model());
    }

    [Fact]
    public async Task GenerateScript_CreateTable()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);
""");

        var generator = new PostgresScriptGenerator();

        var sql = generator.GenerateScript(comparison);

        Assert.Contains("CREATE TABLE \"film\"", sql);
        Assert.Contains("NOT NULL", sql);
        Assert.Contains("varchar(255)", sql);
        // The parser now emits the PK as a first-class element, so it scripts.
        Assert.Contains("PRIMARY KEY", sql);
    }

    [Fact]
    public async Task GenerateScript_CreateTableWithIndex()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);

CREATE INDEX idx_film_title ON film (title);
""");

        var generator = new PostgresScriptGenerator();

        var sql = generator.GenerateScript(comparison);

        Assert.Contains("CREATE INDEX \"idx_film_title\" ON \"film\"", sql);
        Assert.Contains("(\"title\")", sql);
    }

    [Fact]
    public async Task GenerateScript_UniqueIndexWithMethodAndDirection()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE account
(
    account_id integer PRIMARY KEY,
    email      varchar(255) NOT NULL
);

CREATE UNIQUE INDEX idx_account_email ON account USING btree (email DESC NULLS LAST);
""");

        var generator = new PostgresScriptGenerator();

        var sql = generator.GenerateScript(comparison);

        Assert.Contains("CREATE UNIQUE INDEX \"idx_account_email\" ON \"account\" USING btree", sql);
        Assert.Contains("\"email\" DESC NULLS LAST", sql);
    }
}

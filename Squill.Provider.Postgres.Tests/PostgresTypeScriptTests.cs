using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Fast, Docker-free tests over script generation for <c>CREATE TYPE ... AS ENUM</c> and
/// <c>CREATE DOMAIN</c> (issue #75). Models are built with the parser-based model builder and
/// diffed against an empty target, so every object becomes a CreateDelta.
/// </summary>
public class PostgresTypeScriptTests
{
    private static async Task<Model> BuildModelAsync(string sql)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        return (await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken)).Model;
    }

    private static async Task<string> ScriptAgainstEmptyAsync(string sql)
    {
        var model = await BuildModelAsync(sql);
        var provider = new PostgresDatabaseProvider("Host=unused");

        return new PostgresScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, model, new Model()));
    }

    [Fact]
    public async Task CreateEnumType_ScriptsAllLabelsInOrder()
    {
        var sql = await ScriptAgainstEmptyAsync(
            "CREATE TYPE mpaa_rating AS ENUM ('G', 'PG', 'PG-13', 'R', 'NC-17');");

        Assert.Contains(
            "CREATE TYPE \"mpaa_rating\" AS ENUM ('G', 'PG', 'PG-13', 'R', 'NC-17');", sql);
    }

    [Fact]
    public async Task CreateEnumType_SchemaQualifiesNonPublicSchema()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE SCHEMA inventory;
            CREATE TYPE inventory.status AS ENUM ('active', 'retired');
            """);

        Assert.Contains("CREATE TYPE \"inventory\".\"status\" AS ENUM ('active', 'retired');", sql);
    }

    [Fact]
    public async Task CreateDomain_ScriptsBaseTypeAndCheck()
    {
        var sql = await ScriptAgainstEmptyAsync(
            "CREATE DOMAIN year AS integer CONSTRAINT year_check CHECK (VALUE >= 1901 AND VALUE <= 2155);");

        Assert.Contains("CREATE DOMAIN \"year\" AS integer CHECK (", sql);
        Assert.Contains("1901", sql);
        Assert.Contains("2155", sql);
    }

    [Fact]
    public async Task CreateDomain_WithoutCheck_ScriptsBareType()
    {
        var sql = await ScriptAgainstEmptyAsync("CREATE DOMAIN us_postal_code AS text;");

        Assert.Contains("CREATE DOMAIN \"us_postal_code\" AS text;", sql);
        Assert.DoesNotContain("CHECK", sql);
    }

    [Fact]
    public async Task Type_IsScriptedBeforeTheTableThatUsesIt()
    {
        // The type must be created before the table whose column references it, or the
        // CREATE TABLE fails. Verified via ordering in the generated script.
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE TYPE mpaa_rating AS ENUM ('G', 'PG');
            CREATE DOMAIN year AS integer CHECK (VALUE >= 1901);
            CREATE TABLE film (
                film_id integer PRIMARY KEY,
                rating mpaa_rating,
                release_year year
            );
            """);

        var enumIndex = sql.IndexOf("CREATE TYPE", StringComparison.Ordinal);
        var domainIndex = sql.IndexOf("CREATE DOMAIN", StringComparison.Ordinal);
        var tableIndex = sql.IndexOf("CREATE TABLE", StringComparison.Ordinal);

        Assert.True(enumIndex >= 0 && domainIndex >= 0 && tableIndex >= 0);
        Assert.True(enumIndex < tableIndex, "enum type should be created before the table");
        Assert.True(domainIndex < tableIndex, "domain should be created before the table");
    }
}

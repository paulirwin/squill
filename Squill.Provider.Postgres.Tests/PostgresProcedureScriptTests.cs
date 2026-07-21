using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Fast, Docker-free tests over procedure script generation. Models are built with the
/// parser-based model builder and diffed against an empty target, so every procedure
/// becomes a CreateDelta.
/// </summary>
public class PostgresProcedureScriptTests
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
    public async Task CreateProcedure_ScriptsBodyAndLanguage()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE PROCEDURE add_widget(name text)
            LANGUAGE plpgsql
            AS $$
            BEGIN
              INSERT INTO widgets (name) VALUES (name);
            END;
            $$;
            """);

        Assert.Contains("CREATE OR REPLACE PROCEDURE \"add_widget\"(IN name text)", sql);
        Assert.Contains("LANGUAGE \"plpgsql\"", sql);
        Assert.Contains("INSERT INTO widgets (name) VALUES (name);", sql);
    }

    [Fact]
    public async Task CreateProcedure_SchemaQualifiesNonPublicSchema()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE SCHEMA staging;
            CREATE PROCEDURE staging.reload() LANGUAGE sql AS $$ SELECT 1 $$;
            """);

        Assert.Contains("CREATE OR REPLACE PROCEDURE \"staging\".\"reload\"()", sql);
    }

    [Fact]
    public async Task CreateProcedure_ScriptsSecurityDefiner()
    {
        var sql = await ScriptAgainstEmptyAsync(
            "CREATE PROCEDURE p() LANGUAGE plpgsql SECURITY DEFINER AS $$ BEGIN NULL; END; $$;");

        Assert.Contains("SECURITY DEFINER", sql);
    }

    [Fact]
    public async Task CreateProcedure_OmitsSecurityDefinerWhenInvoker()
    {
        var sql = await ScriptAgainstEmptyAsync(
            "CREATE PROCEDURE p() LANGUAGE sql AS $$ SELECT 1 $$;");

        Assert.DoesNotContain("SECURITY", sql);
    }

    // Dollar quoting has no escape sequence, so a body that itself contains $$ must be
    // wrapped in a tag that does not collide with it or the statement would end early.
    [Fact]
    public async Task CreateProcedure_ChoosesANonCollidingDollarQuoteTag()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE PROCEDURE p()
            LANGUAGE plpgsql
            AS $outer$
            BEGIN
              EXECUTE $$ SELECT 1 $$;
            END;
            $outer$;
            """);

        Assert.Contains("$squill$", sql);
        Assert.Contains("EXECUTE $$ SELECT 1 $$;", sql);
    }

    // A procedure is created after tables so a body that reads or writes one works on a
    // first deploy, when both are created in the same script.
    [Fact]
    public async Task CreateProcedure_IsScriptedAfterTables()
    {
        var sql = await ScriptAgainstEmptyAsync("""
            CREATE PROCEDURE touch_widgets() LANGUAGE sql AS $$ SELECT 1 $$;

            CREATE TABLE widgets (id integer PRIMARY KEY);
            """);

        Assert.True(
            sql.IndexOf("CREATE TABLE", StringComparison.Ordinal)
            < sql.IndexOf("CREATE OR REPLACE PROCEDURE", StringComparison.Ordinal),
            $"Expected the table to be created before the procedure, but got:\n{sql}");
    }

    // A changed body is redefined in place: PostgreSQL has no ALTER PROCEDURE for a body,
    // and dropping first would briefly remove a procedure other sessions may be calling.
    [Fact]
    public async Task ChangedProcedureBody_IsReplacedInPlace()
    {
        var target = await BuildModelAsync(
            "CREATE PROCEDURE p() LANGUAGE sql AS $$ SELECT 1 $$;");
        var source = await BuildModelAsync(
            "CREATE PROCEDURE p() LANGUAGE sql AS $$ SELECT 2 $$;");

        var provider = new PostgresDatabaseProvider("Host=unused");
        var sql = new PostgresScriptGenerator()
            .GenerateScript(SchemaCompare.Compare(provider, source, target));

        Assert.Contains("CREATE OR REPLACE PROCEDURE", sql);
        Assert.Contains("SELECT 2", sql);
        Assert.DoesNotContain("DROP PROCEDURE", sql);
    }

    [Fact]
    public async Task UnchangedProcedure_ProducesNoScript()
    {
        const string definition = "CREATE PROCEDURE p(a integer) LANGUAGE sql AS $$ SELECT a $$;";

        var provider = new PostgresDatabaseProvider("Host=unused");
        var comparison = SchemaCompare.Compare(
            provider, await BuildModelAsync(definition), await BuildModelAsync(definition));

        Assert.Empty(comparison.Deltas);
    }

    // Overloads are distinct objects, so adding one leaves the other alone.
    [Fact]
    public async Task AddedOverload_IsCreatedWithoutTouchingTheExisting()
    {
        var target = await BuildModelAsync(
            "CREATE PROCEDURE p(a integer) LANGUAGE sql AS $$ SELECT a $$;");

        var source = await BuildModelAsync("""
            CREATE PROCEDURE p(a integer) LANGUAGE sql AS $$ SELECT a $$;
            CREATE PROCEDURE p(a text) LANGUAGE sql AS $$ SELECT a $$;
            """);

        var provider = new PostgresDatabaseProvider("Host=unused");
        var comparison = SchemaCompare.Compare(provider, source, target);

        var delta = Assert.IsType<CreateDelta>(Assert.Single(comparison.Deltas));
        Assert.Equal("public.p(text)", delta.Element.Name);
    }

    [Fact]
    public async Task DroppedProcedure_IncludesItsSignature()
    {
        var target = await BuildModelAsync(
            "CREATE PROCEDURE p(a integer, b text) LANGUAGE sql AS $$ SELECT 1 $$;");

        var provider = new PostgresDatabaseProvider("Host=unused");
        var comparison = SchemaCompare.Compare(
            provider, new Model(), target, new DeployOptions { DropObjectsNotInSource = true });

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("DROP PROCEDURE IF EXISTS \"p\"(integer,text);", sql);
    }
}

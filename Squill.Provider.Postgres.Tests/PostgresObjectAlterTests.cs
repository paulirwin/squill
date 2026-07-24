using Squill.Core;
using Squill.PostgresParser;
using Squill.TestFramework;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Docker-free tests over altering an existing enum, domain, aggregate, schema or trigger
/// (issue #122). Before this, any of these changing between builds hit a
/// <c>NotImplementedException</c> in <c>SchemaCompare.DiffExistingElement</c>, so an
/// incremental deploy of a redefined enum or domain failed outright.
///
/// The source (desired) model is parsed from SQL; the target (current) model stands in for
/// what the database extractor produces. End-to-end behavior is covered by the integration
/// tests.
/// </summary>
public class PostgresObjectAlterTests
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

    // ---- Enum types ----

    // Labels appended to an enum are added in place. DROP TYPE + CREATE TYPE would fail
    // whenever a column is typed as the enum, so the only workable form is ALTER TYPE.
    [Fact]
    public async Task EnumType_AppendedLabel_EmitsAlterTypeAddValue()
    {
        var (comparison, sql) = await DiffAsync(
            "CREATE TYPE mpaa_rating AS ENUM ('G', 'PG', 'PG-13');",
            "CREATE TYPE mpaa_rating AS ENUM ('G', 'PG');");

        Assert.Single(comparison.Deltas);
        Assert.Contains("ALTER TYPE \"mpaa_rating\" ADD VALUE 'PG-13';", sql);
        Assert.DoesNotContain("DROP TYPE", sql);
    }

    [Fact]
    public async Task EnumType_SeveralAppendedLabels_EmitsOneAddValuePerLabelInOrder()
    {
        var (_, sql) = await DiffAsync(
            "CREATE TYPE mpaa_rating AS ENUM ('G', 'PG', 'PG-13', 'R');",
            "CREATE TYPE mpaa_rating AS ENUM ('G', 'PG');");

        var addPg13 = sql.IndexOf("ADD VALUE 'PG-13'", StringComparison.Ordinal);
        var addR = sql.IndexOf("ADD VALUE 'R'", StringComparison.Ordinal);

        Assert.True(addPg13 >= 0 && addR >= 0, $"Expected both ADD VALUEs in:{Environment.NewLine}{sql}");
        Assert.True(addPg13 < addR, "Labels must be added in declaration order");
    }

    // A label inserted in the middle keeps the enum's declared order, which ADD VALUE alone
    // cannot express without saying where — so it is added BEFORE its successor.
    [Fact]
    public async Task EnumType_InsertedLabel_EmitsAddValueBeforeSuccessor()
    {
        var (_, sql) = await DiffAsync(
            "CREATE TYPE mpaa_rating AS ENUM ('G', 'PG', 'PG-13');",
            "CREATE TYPE mpaa_rating AS ENUM ('G', 'PG-13');");

        Assert.Contains("ALTER TYPE \"mpaa_rating\" ADD VALUE 'PG' BEFORE 'PG-13';", sql);
    }

    [Fact]
    public async Task EnumType_SchemaQualifiesNonPublicSchema()
    {
        var (_, sql) = await DiffAsync(
            """
            CREATE SCHEMA inventory;
            CREATE TYPE inventory.status AS ENUM ('active', 'retired', 'lost');
            """,
            """
            CREATE SCHEMA inventory;
            CREATE TYPE inventory.status AS ENUM ('active', 'retired');
            """);

        Assert.Contains("ALTER TYPE \"inventory\".\"status\" ADD VALUE 'lost';", sql);
    }

    // Removing a label cannot be scripted: PostgreSQL has no ALTER TYPE ... DROP VALUE, and
    // rebuilding the type would destroy the data in every column using it. Failing loudly is
    // correct — the alternative is silently deploying a schema that does not match source.
    [Fact]
    public async Task EnumType_RemovedLabel_FailsWithActionableMessage()
    {
        var source = await ParseModelAsync("CREATE TYPE mpaa_rating AS ENUM ('G', 'PG');");
        var target = await ParseModelAsync("CREATE TYPE mpaa_rating AS ENUM ('G', 'PG', 'R');");

        var comparison = SchemaCompare.Compare(Provider, source, target);

        var ex = Assert.Throws<NotSupportedException>(
            () => new PostgresScriptGenerator().GenerateScript(comparison));

        Assert.Contains("mpaa_rating", ex.Message);
        Assert.Contains("'R'", ex.Message);
    }

    [Fact]
    public async Task EnumType_ReorderedLabels_FailsWithActionableMessage()
    {
        var source = await ParseModelAsync("CREATE TYPE mpaa_rating AS ENUM ('PG', 'G');");
        var target = await ParseModelAsync("CREATE TYPE mpaa_rating AS ENUM ('G', 'PG');");

        var comparison = SchemaCompare.Compare(Provider, source, target);

        Assert.Throws<NotSupportedException>(
            () => new PostgresScriptGenerator().GenerateScript(comparison));
    }

    [Fact]
    public async Task EnumType_Unchanged_ProducesNoDelta()
    {
        var (comparison, _) = await DiffAsync(
            "CREATE TYPE mpaa_rating AS ENUM ('G', 'PG');",
            "CREATE TYPE mpaa_rating AS ENUM ('G', 'PG');");

        Assert.Empty(comparison.Deltas);
    }

    // ---- Domains ----

    // PostgreSQL cannot change a domain's base type: ALTER DOMAIN has no TYPE form, and the
    // domain cannot be dropped while a column uses it. The diff records the change so the
    // failure names both types and what to do, instead of the bare NotImplementedException
    // naming only the element type that SchemaCompare used to throw.
    [Fact]
    public async Task Domain_ChangedBaseType_FailsWithActionableMessage()
    {
        var source = await ParseModelAsync("CREATE DOMAIN postal_code AS varchar(10);");
        var target = await ParseModelAsync("CREATE DOMAIN postal_code AS varchar(5);");

        var comparison = SchemaCompare.Compare(Provider, source, target);

        Assert.Single(comparison.Deltas);

        var ex = Assert.Throws<NotSupportedException>(
            () => new PostgresScriptGenerator().GenerateScript(comparison));

        Assert.Contains("postal_code", ex.Message);
        Assert.Contains("varchar(10)", ex.Message);
        Assert.Contains("varchar(5)", ex.Message);
    }

    [Fact]
    public async Task Domain_Unchanged_ProducesNoDelta()
    {
        var (comparison, _) = await DiffAsync(
            "CREATE DOMAIN postal_code AS varchar(10);",
            "CREATE DOMAIN postal_code AS varchar(10);");

        Assert.Empty(comparison.Deltas);
    }

    // ---- Aggregates ----

    // An aggregate has no CREATE OR REPLACE form in PostgreSQL, so a changed definition is
    // deployed as DROP + CREATE. The state function is what changes here: an aggregate's
    // modeled identity is its name, argument types, SFUNC and STYPE.
    [Fact]
    public async Task Aggregate_ChangedDefinition_EmitsDropThenCreate()
    {
        const string sfuncs = """
            CREATE FUNCTION add_ints(integer, integer) RETURNS integer
                AS $$ SELECT $1 + $2; $$ LANGUAGE sql;
            CREATE FUNCTION max_ints(integer, integer) RETURNS integer
                AS $$ SELECT greatest($1, $2); $$ LANGUAGE sql;
            """;

        var (comparison, sql) = await DiffAsync(
            sfuncs + """

            CREATE AGGREGATE total(integer) (SFUNC = max_ints, STYPE = integer);
            """,
            sfuncs + """

            CREATE AGGREGATE total(integer) (SFUNC = add_ints, STYPE = integer);
            """);

        Assert.Contains(comparison.Deltas, d => d is RecreateDelta);

        var drop = sql.IndexOf("DROP AGGREGATE", StringComparison.Ordinal);
        var create = sql.IndexOf("CREATE AGGREGATE", StringComparison.Ordinal);

        Assert.True(drop >= 0, $"Expected a DROP AGGREGATE in:{Environment.NewLine}{sql}");
        Assert.True(create > drop, "The aggregate must be dropped before it is recreated");
    }

    // ---- Triggers ----

    // Postgres omitted triggers from its replaceable set while MariaDB included them, so a
    // changed Postgres trigger threw where the same change deployed fine on MariaDB. A
    // trigger is now recreated as DROP + CREATE on both (issue #122). CREATE OR REPLACE
    // TRIGGER exists only from PG 14, so DROP + CREATE is the portable spelling.
    [Fact]
    public async Task Trigger_ChangedDefinition_EmitsDropThenCreate()
    {
        const string preamble = """
            CREATE TABLE film (film_id integer NOT NULL, last_update timestamp NOT NULL);
            CREATE FUNCTION touch_row() RETURNS trigger
                AS $$ BEGIN RETURN NEW; END; $$ LANGUAGE plpgsql;
            """;

        var (comparison, sql) = await DiffAsync(
            preamble + """

            CREATE TRIGGER film_touch BEFORE UPDATE ON film
                FOR EACH ROW EXECUTE FUNCTION touch_row();
            """,
            preamble + """

            CREATE TRIGGER film_touch BEFORE INSERT ON film
                FOR EACH ROW EXECUTE FUNCTION touch_row();
            """);

        Assert.Contains(comparison.Deltas, d => d is RecreateDelta);

        var drop = sql.IndexOf("DROP TRIGGER IF EXISTS", StringComparison.Ordinal);
        var create = sql.IndexOf("CREATE TRIGGER", StringComparison.Ordinal);

        Assert.True(drop >= 0, $"Expected a DROP TRIGGER in:{Environment.NewLine}{sql}");
        Assert.True(create > drop, "The trigger must be dropped before it is recreated");
        Assert.Contains("BEFORE UPDATE", sql);
    }

    [Fact]
    public async Task Trigger_Unchanged_ProducesNoDelta()
    {
        const string sql = """
            CREATE TABLE film (film_id integer NOT NULL, last_update timestamp NOT NULL);
            CREATE FUNCTION touch_row() RETURNS trigger
                AS $$ BEGIN RETURN NEW; END; $$ LANGUAGE plpgsql;
            CREATE TRIGGER film_touch BEFORE UPDATE ON film
                FOR EACH ROW EXECUTE FUNCTION touch_row();
            """;

        var (comparison, _) = await DiffAsync(sql, sql);

        Assert.Empty(comparison.Deltas);
    }

    // ---- Schemas ----

    // A schema element carries nothing beyond its name, and its name is its identity, so two
    // schema elements that compare as "the same element, changed" cannot actually exist. The
    // guard is that this does not throw.
    [Fact]
    public async Task Schema_Unchanged_ProducesNoDelta()
    {
        var (comparison, _) = await DiffAsync("CREATE SCHEMA inventory;", "CREATE SCHEMA inventory;");

        Assert.Empty(comparison.Deltas);
    }
}

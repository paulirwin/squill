using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;
using Squill.TestFramework;

namespace Squill.IntegrationTests.Postgres.ObjectAlterTest;

// End-to-end coverage for altering an existing enum, domain and trigger (issue #122). Before
// this, any of these changing between deploys threw NotImplementedException out of
// SchemaCompare, so an incremental deploy of a redefined enum failed outright.
//
// Each test deploys an initial schema to a real database, then deploys a changed one against
// it and asserts both that the generated DDL is valid, executable Postgres and that the
// database converges on the declared state. The enum tests are the load-bearing ones: the
// enum is used by a table column that already holds rows, which is exactly the case a
// DROP TYPE + CREATE TYPE rebuild could not survive.
public class PostgresObjectAlterTest : PostgresIntegrationTestBase
{
    private static Task<Model> ParseModelAsync(string sql, CancellationToken cancellationToken)
        => WorkspaceModelBuilding.BuildModelAsync(
            sql,
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            cancellationToken);

    // Deploys initialSql to a fresh database, runs seedSql (if any) against it, then deploys
    // changedSql on top and returns the still-open database for assertions.
    private async Task<(IDatabase Database, IDatabaseProvider Provider)> DeployThenRedeployAsync(
        string initialSql,
        string? seedSql,
        string changedSql,
        CancellationToken cancellationToken)
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var database = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);

        var modelBuilder = provider.CreateDatabaseModelBuilder(database);

        var initialModel = await ParseModelAsync(initialSql, cancellationToken);

        await database.PublishAsync(
            SchemaCompare.Compare(provider, initialModel,
                await modelBuilder.ExtractModelAsync(cancellationToken)),
            cancellationToken);

        if (seedSql is not null)
        {
            await database.RunScriptAsync(seedSql, cancellationToken: cancellationToken);
        }

        var changedModel = await ParseModelAsync(changedSql, cancellationToken);

        // The second deploy diffs against the live database, so this is the incremental path
        // that used to throw.
        await database.PublishAsync(
            SchemaCompare.Compare(provider, changedModel,
                await modelBuilder.ExtractModelAsync(cancellationToken)),
            cancellationToken);

        return (database, provider);
    }

    private static async Task<List<string>> QueryStringsAsync(
        IDatabase database, string sql, CancellationToken cancellationToken)
    {
        var results = new List<string>();

        await using var reader = await database.RunScriptReaderAsync(
            sql, cancellationToken: cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    // A label appended to an enum that a populated table column already uses. This is the
    // case that proves ALTER TYPE ... ADD VALUE is the right script: dropping and recreating
    // the type would fail against the dependent column, and would destroy the existing row.
    [Fact]
    public async Task EnumType_AppendedLabel_AltersInPlaceAndPreservesData()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string initial = """
            CREATE TYPE mpaa_rating AS ENUM ('G', 'PG');
            CREATE TABLE film (film_id integer NOT NULL, rating mpaa_rating NOT NULL);
            """;

        const string changed = """
            CREATE TYPE mpaa_rating AS ENUM ('G', 'PG', 'PG-13', 'R');
            CREATE TABLE film (film_id integer NOT NULL, rating mpaa_rating NOT NULL);
            """;

        var (database, provider) = await DeployThenRedeployAsync(
            initial,
            "INSERT INTO film (film_id, rating) VALUES (1, 'PG');",
            changed,
            cancellationToken);

        try
        {
            // The new labels exist, in declaration order (an enum's order is its sort order).
            var labels = await QueryStringsAsync(
                database,
                """
                SELECT e.enumlabel FROM pg_enum e
                JOIN pg_type t ON t.oid = e.enumtypid
                WHERE t.typname = 'mpaa_rating'
                ORDER BY e.enumsortorder;
                """,
                cancellationToken);

            Assert.Equal(["G", "PG", "PG-13", "R"], labels);

            // The pre-existing row survived — the type was altered, not rebuilt.
            var ratings = await QueryStringsAsync(
                database, "SELECT rating::text FROM film WHERE film_id = 1;", cancellationToken);

            Assert.Equal(["PG"], ratings);

            // The newly added label is usable, proving the ALTER committed.
            await database.RunScriptAsync(
                "INSERT INTO film (film_id, rating) VALUES (2, 'R');",
                cancellationToken: cancellationToken);

            // Redeploying the same model is a no-op: the enum now hash-matches the database.
            var modelBuilder = provider.CreateDatabaseModelBuilder(database);
            var comparison = SchemaCompare.Compare(
                provider,
                await ParseModelAsync(changed, cancellationToken),
                await modelBuilder.ExtractModelAsync(cancellationToken));

            Assert.Empty(comparison.Deltas);
        }
        finally
        {
            await database.DropAsync(cancellationToken);
        }
    }

    // A label inserted between two existing ones must land in the declared position, since an
    // enum's order is its sort order. This exercises the ADD VALUE ... BEFORE form.
    [Fact]
    public async Task EnumType_InsertedLabel_LandsInDeclaredPosition()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string initial = "CREATE TYPE mpaa_rating AS ENUM ('G', 'R');";
        const string changed = "CREATE TYPE mpaa_rating AS ENUM ('G', 'PG', 'PG-13', 'R');";

        var (database, _) = await DeployThenRedeployAsync(initial, null, changed, cancellationToken);

        try
        {
            var labels = await QueryStringsAsync(
                database,
                """
                SELECT e.enumlabel FROM pg_enum e
                JOIN pg_type t ON t.oid = e.enumtypid
                WHERE t.typname = 'mpaa_rating'
                ORDER BY e.enumsortorder;
                """,
                cancellationToken);

            Assert.Equal(["G", "PG", "PG-13", "R"], labels);
        }
        finally
        {
            await database.DropAsync(cancellationToken);
        }
    }

    // A domain that has not changed redeploys as a no-op against a live database. This is the
    // property that keeps a domain out of the alter path entirely: its base type round-trips
    // exactly, and its CHECK is excluded from identity because PostgreSQL rewrites the stored
    // predicate. Without both, every redeploy would show a delta and then fail, since a
    // domain's base type cannot be altered at all.
    [Fact]
    public async Task Domain_Unchanged_RedeploysAsNoOp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        // The length modifier is the point: format_type renders the base type as
        // `character varying(5)`, while the parser carries `character varying` + Length = 5.
        // Until the extractor split those apart, this domain differed on every redeploy — and
        // because a domain's base type cannot be altered, the redeploy then failed outright.
        const string sql = """
            CREATE DOMAIN postal_code AS varchar(5) CHECK (VALUE <> '');
            CREATE TABLE address (address_id integer NOT NULL, postcode postal_code NOT NULL);
            """;

        var (database, provider) = await DeployThenRedeployAsync(sql, null, sql, cancellationToken);

        try
        {
            var modelBuilder = provider.CreateDatabaseModelBuilder(database);

            var comparison = SchemaCompare.Compare(
                provider,
                await ParseModelAsync(sql, cancellationToken),
                await modelBuilder.ExtractModelAsync(cancellationToken));

            Assert.Empty(comparison.Deltas);

            // The domain's CHECK is live in the database, not merely modeled.
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => database.RunScriptAsync(
                "INSERT INTO address (address_id, postcode) VALUES (1, '');",
                cancellationToken: cancellationToken));
        }
        finally
        {
            await database.DropAsync(cancellationToken);
        }
    }

    // The same round-trip for a numeric domain, whose base type carries two modifiers rather
    // than one — format_type renders `numeric(10,2)`, which must split into Precision and Scale.
    [Fact]
    public async Task NumericDomain_Unchanged_RedeploysAsNoOp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string sql = """
            CREATE DOMAIN money_amount AS numeric(10, 2);
            CREATE TABLE payment (payment_id integer NOT NULL, amount money_amount NOT NULL);
            """;

        var (database, provider) = await DeployThenRedeployAsync(sql, null, sql, cancellationToken);

        try
        {
            var modelBuilder = provider.CreateDatabaseModelBuilder(database);

            var comparison = SchemaCompare.Compare(
                provider,
                await ParseModelAsync(sql, cancellationToken),
                await modelBuilder.ExtractModelAsync(cancellationToken));

            Assert.Empty(comparison.Deltas);
        }
        finally
        {
            await database.DropAsync(cancellationToken);
        }
    }

    // Changing a domain's base type is reported, not silently mis-scripted. PostgreSQL has no
    // ALTER DOMAIN ... TYPE, and the domain cannot be dropped while the column uses it, so
    // failing with an actionable message is the correct outcome.
    [Fact]
    public async Task Domain_ChangedBaseType_FailsRatherThanEmittingInvalidSql()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string initial = """
            CREATE DOMAIN postal_code AS varchar(5);
            CREATE TABLE address (address_id integer NOT NULL, postcode postal_code NOT NULL);
            """;

        const string changed = """
            CREATE DOMAIN postal_code AS varchar(10);
            CREATE TABLE address (address_id integer NOT NULL, postcode postal_code NOT NULL);
            """;

        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var database = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", cancellationToken);

        try
        {
            var modelBuilder = provider.CreateDatabaseModelBuilder(database);

            await database.PublishAsync(
                SchemaCompare.Compare(provider,
                    await ParseModelAsync(initial, cancellationToken),
                    await modelBuilder.ExtractModelAsync(cancellationToken)),
                cancellationToken);

            var comparison = SchemaCompare.Compare(
                provider,
                await ParseModelAsync(changed, cancellationToken),
                await modelBuilder.ExtractModelAsync(cancellationToken));

            var ex = Assert.Throws<NotSupportedException>(
                () => new PostgresScriptGenerator().GenerateScript(comparison));

            Assert.Contains("postal_code", ex.Message);
        }
        finally
        {
            await database.DropAsync(cancellationToken);
        }
    }

    // A trigger whose definition changed is dropped and recreated. Postgres previously threw
    // here while MariaDB deployed the same change (issue #122).
    [Fact]
    public async Task Trigger_ChangedDefinition_IsRecreated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        const string preamble = """
            CREATE TABLE film (film_id integer NOT NULL, last_update timestamp);
            CREATE FUNCTION touch_row() RETURNS trigger
                AS $$ BEGIN NEW.last_update = now(); RETURN NEW; END; $$ LANGUAGE plpgsql;
            """;

        const string initial = preamble + """

            CREATE TRIGGER film_touch BEFORE INSERT ON film
                FOR EACH ROW EXECUTE FUNCTION touch_row();
            """;

        const string changed = preamble + """

            CREATE TRIGGER film_touch BEFORE INSERT OR UPDATE ON film
                FOR EACH ROW EXECUTE FUNCTION touch_row();
            """;

        var (database, provider) = await DeployThenRedeployAsync(
            initial, null, changed, cancellationToken);

        try
        {
            // pg_get_triggerdef reflects the redefined trigger, so the recreate took effect.
            var definitions = await QueryStringsAsync(
                database,
                """
                SELECT pg_get_triggerdef(t.oid) FROM pg_trigger t
                WHERE t.tgname = 'film_touch' AND NOT t.tgisinternal;
                """,
                cancellationToken);

            var definition = Assert.Single(definitions);
            Assert.Contains("UPDATE", definition);

            // And the redeploy converges: the trigger now matches the declared source.
            var modelBuilder = provider.CreateDatabaseModelBuilder(database);
            var comparison = SchemaCompare.Compare(
                provider,
                await ParseModelAsync(changed, cancellationToken),
                await modelBuilder.ExtractModelAsync(cancellationToken));

            Assert.Empty(comparison.Deltas);
        }
        finally
        {
            await database.DropAsync(cancellationToken);
        }
    }
}

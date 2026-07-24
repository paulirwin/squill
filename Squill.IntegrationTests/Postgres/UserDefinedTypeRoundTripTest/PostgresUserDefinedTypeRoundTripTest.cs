using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;
using Squill.TestFramework;

namespace Squill.IntegrationTests.Postgres.UserDefinedTypeRoundTripTest;

// Full round trip for user-defined types (issue #75, #84): build a model from SQL (a CREATE
// TYPE ... AS ENUM, a CREATE DOMAIN with a named CHECK, and a table with both an enum-typed
// and a domain-typed column) against a temporary database, publish it to a fresh target,
// re-extract the target's model, and assert the model hashes match. It then inserts and reads
// back an enum value to prove the emitted DDL is valid, executable Postgres.
//
// The enum type's identity is its name and its ordered labels; the domain's identity is its
// name and base type (its CHECK text is carried for scripting only, since PostgreSQL rewrites
// the predicate — see PostgresModelFactory.CreateDomain). A domain-typed column's type
// specifier is the domain name, which the DB-extraction builder resolves from the catalog
// rather than the base type information_schema reports (issue #84). So a re-extracted database
// hashes equal to the source model.
public class PostgresUserDefinedTypeRoundTripTest : PostgresIntegrationTestBase
{
    [Fact]
    public async Task UserDefinedTypeRoundTrip_ModelHashesMatchAndInsertWorks()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var modelBuilder = new TemporaryDatabaseModelBuilder(provider);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.UserDefinedTypeRoundTripTest.WithTypes.sql",
            FileKind.Compile));

        var model = await modelBuilder.BuildModelAsync(workspace, TestContext.Current.CancellationToken);

        // Sanity-check the built model: the enum and domain are modeled as their own elements.
        var enumType = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlEnumType);
        Assert.Equal(["G", "PG", "PG-13", "R", "NC-17"], PostgresModelFactory.GetEnumLabels(enumType));

        var domain = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlDomain);
        Assert.Equal("year", domain.Name);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            var comparison = SchemaCompare.Compare(provider, model, emptyModel);

            await testDb.PublishAsync(comparison, TestContext.Current.CancellationToken);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            // The published database, re-extracted, must hash-match the source model —
            // proving the enum type and domain round-trip exactly (parser model == DB model).
            Assert.True(HashUtility.HashesEqual(model.Hash, publishedModel.Hash),
                "Model hashes do not match");

            await AssertEnumInsertWorksAsync(testDb, TestContext.Current.CancellationToken);
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    // A parser-built model with a domain-typed column round-trips through a real database and
    // redeploys as a no-op (issue #84). This drives the parser model builder directly — the
    // direction the project is moving — and asserts idempotency, the property the round-trip
    // breaks without the domain-name resolution in the DB-extraction builder: the parsed column
    // type is the domain name (`year`), while information_schema reports the base type
    // (`integer`), so without the fix the two hashes diverge and every redeploy shows a delta.
    [Fact]
    public async Task DomainTypedColumn_RoundTripsAndRedeploysNoOp()
        => await RoundTripHarness.AssertRoundTripAsync(
            new PostgresDatabaseProvider(ConnectionString),
            ws => new ParserWorkspaceModelBuilder(ws, new AntlrPostgresParser()),
            """
            CREATE DOMAIN year AS integer
                CONSTRAINT year_check CHECK (VALUE >= 1901 AND VALUE <= 2155);
            CREATE TABLE film (
                film_id integer PRIMARY KEY,
                release_year year
            );
            """,
            "postgres",
            assertRedeployNoOp: true,
            TestContext.Current.CancellationToken);

    // Inserts a row using an enum literal and reads it back, confirming the published schema
    // is functional: the enum-typed column accepts and returns an enum value.
    private static async Task AssertEnumInsertWorksAsync(IDatabase database, CancellationToken cancellationToken)
    {
        await database.ConnectAsync(cancellationToken);

        await database.RunScriptAsync(
            "INSERT INTO film (film_id, title, rating) VALUES (1, 'Test', 'PG-13');",
            cancellationToken: cancellationToken);

        // Cast the enum to text so Npgsql returns it as a plain string (an unmapped enum
        // type would otherwise fail to bind to string).
        const string query = "SELECT rating::text FROM film WHERE film_id = 1;";

        await using var reader = await database.RunScriptReaderAsync(query, cancellationToken: cancellationToken);

        Assert.True(await reader.ReadAsync(cancellationToken), "Query returned no rows");
        Assert.Equal("PG-13", reader.GetString(0));
    }
}

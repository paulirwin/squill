using Squill.Core;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.UserDefinedTypeRoundTripTest;

// Full round trip for user-defined types (issue #75): build a model from SQL (a CREATE TYPE
// ... AS ENUM, a CREATE DOMAIN with a named CHECK, and a table whose column is the enum)
// against a temporary database, publish it to a fresh target, re-extract the target's model,
// and assert the model hashes match. It then inserts and reads back an enum value to prove
// the emitted DDL is valid, executable Postgres.
//
// The enum type's identity is its name and its ordered labels; the domain's identity is its
// name and base type (its CHECK text is carried for scripting only, since PostgreSQL rewrites
// the predicate — see PostgresModelFactory.CreateDomain). So a re-extracted database hashes
// equal to the source model.
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

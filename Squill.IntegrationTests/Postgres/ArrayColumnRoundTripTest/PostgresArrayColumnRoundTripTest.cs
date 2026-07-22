using Squill.Core;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.ArrayColumnRoundTripTest;

// Full round trip for array column types (issue #76): build a model from SQL (a table
// with text[], varchar[] and integer[] columns) against a temporary database, publish it
// to a fresh target database, re-extract the target's model, and assert the model hashes
// match. It then inserts and reads back an array value to prove the emitted DDL is valid,
// executable Postgres.
//
// PostgreSQL declares an array by appending `[]` to the element type's name and "ignores
// any supplied array size limits" (the arrays docs), so the parser and the DB builder both
// represent the type as the element's canonical name with `[]` appended (e.g. "text[]"),
// which is exactly what format_type() renders on the database side — so the hashes agree.
public class PostgresArrayColumnRoundTripTest : PostgresIntegrationTestBase
{
    [Fact]
    public async Task ArrayColumnRoundTrip_ModelHashesMatchAndInsertWorks()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var modelBuilder = new TemporaryDatabaseModelBuilder(provider);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.ArrayColumnRoundTripTest.WithArrays.sql", FileKind.Compile));

        var model = await modelBuilder.BuildModelAsync(workspace, TestContext.Current.CancellationToken);

        // Sanity-check the built model: the array column's type specifier renders the
        // canonical array notation and carries no size property.
        var table = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlTable);
        var columns = table.Relationships.Single(r => r.Name == PostgresRelationshipNames.Columns);
        var featuresCol = Assert.IsType<Element>(
            columns.Entries.OfType<Element>().Single(c => c.Name?.EndsWith("special_features") == true));
        var typeElem = Assert.IsType<Element>(
            featuresCol.Relationships.Single(r => r.Name == PostgresRelationshipNames.TypeSpecifier).Entries[0]);
        var typeRef = Assert.IsType<Reference>(
            typeElem.Relationships.Single(r => r.Name == PostgresRelationshipNames.Type).Entries[0]);
        Assert.Equal("text[]", typeRef.Name);
        Assert.Empty(typeElem.Properties);

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
            // proving the array types round-trip exactly (parser model == DB model).
            Assert.True(HashUtility.HashesEqual(model.Hash, publishedModel.Hash), "Model hashes do not match");

            await AssertArrayInsertWorksAsync(testDb, TestContext.Current.CancellationToken);
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    // Inserts a row with array literals and reads back one element, confirming the
    // published schema is functional: the array columns accept and return array data.
    private static async Task AssertArrayInsertWorksAsync(IDatabase database, CancellationToken cancellationToken)
    {
        await database.ConnectAsync(cancellationToken);

        await database.RunScriptAsync(
            """
            INSERT INTO films (id, special_features, tags, scores) VALUES
                (1, ARRAY['Trailers', 'Commentaries'], ARRAY['drama', 'classic'], ARRAY[7, 9, 8]);
            """,
            cancellationToken: cancellationToken);

        // special_features[1] is the first element (PostgreSQL arrays are one-based).
        const string query = "SELECT special_features[1] FROM films WHERE id = 1;";

        await using var reader = await database.RunScriptReaderAsync(query, cancellationToken: cancellationToken);

        Assert.True(await reader.ReadAsync(cancellationToken), "Query returned no rows");
        Assert.Equal("Trailers", reader.GetString(0));
    }
}

using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.CircularForeignKeyTest;

public class PostgresCircularForeignKeyTest : PostgresIntegrationTestBase
{
    // Two tables that reference each other cannot both carry their foreign key inline —
    // whichever is created first would reference a table that does not exist yet. The
    // constraint closing the cycle is deferred to an ALTER TABLE once both tables exist.
    // This proves the generated DDL is valid, executable Postgres and that both directions
    // of the cycle survive the round trip.
    //
    // The models are compared by their foreign keys rather than by whole-model hash: the
    // parsed model keeps source declaration order while the database reports its objects
    // sorted by name, so the two only hash-match when the source happens to be written in
    // sorted order. That ordering gap is unrelated to cycle breaking.
    [Fact]
    public async Task CircularForeignKeys_RoundTrip()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.CircularForeignKeyTest.CircularTables.sql",
            FileKind.Compile));

        var model = await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, model, emptyModel),
                TestContext.Current.CancellationToken);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            // Both directions must survive: deferring one constraint must not lose it, and
            // each must point at the table the source declared.
            var foreignKeys = publishedModel.Elements
                .Where(i => i.Type == PostgresElementTypes.SqlForeignKeyConstraint)
                .ToList();

            Assert.Equal(2, foreignKeys.Count);

            Assert.Contains(foreignKeys, i => ReferencedTable(i) == "wife");
            Assert.Contains(foreignKeys, i => ReferencedTable(i) == "husband");
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    private static string? ReferencedTable(Element foreignKey)
        => foreignKey.GetRelationship(PostgresRelationshipNames.ForeignTable)
            ?.Entries.OfType<Reference>().FirstOrDefault()?.Name;
}

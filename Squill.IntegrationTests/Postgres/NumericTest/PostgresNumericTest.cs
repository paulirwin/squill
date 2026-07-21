using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.NumericTest;

/// <summary>
/// Issue #33: a `numeric(p, s)` column must round-trip between the parser-built
/// model and the model re-extracted from a real database. The parser records the
/// type's Precision and Scale; before the fix the DB model builder read neither
/// back from information_schema, so the two models' type-specifier hashes diverged.
///
/// This publishes a parser-built table carrying a `numeric(12, 2)` column into a
/// real database (proving the generated DDL is valid, executable Postgres), then
/// re-extracts and asserts the type specifier matches what the parser produced.
/// </summary>
public class PostgresNumericTest : PostgresIntegrationTestBase
{
    private const string Sql = """
CREATE TABLE prices
(
    amount numeric(12, 2) NOT NULL
);
""";

    [Fact]
    public async Task Numeric_RoundTripsThroughRealPostgres()
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Prices.sql", FileKind.Compile, Sql));
        var parserModel = (await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken)).Model;

        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var db = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        try
        {
            var dbModelBuilder = provider.CreateDatabaseModelBuilder(db);

            // Empty target -> every element is a CreateDelta; publishing runs the
            // generated DDL against real Postgres.
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);
            var comparison = SchemaCompare.Compare(provider, parserModel, emptyModel);
            await db.PublishAsync(comparison, TestContext.Current.CancellationToken);

            var reExtracted = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            // The numeric column round-trips with the same type-specifier shape the
            // parser produced — so a DACPAC-vs-DB model comparison hash-matches.
            Assert.Equal(TypeSpecHash(parserModel, "prices.amount"), TypeSpecHash(reExtracted, "prices.amount"));

            // And the re-extracted type specifically carries the canonical type with
            // the expected precision and scale.
            var amountType = TypeSpecOf(reExtracted, "prices.amount");
            var amountRef = (Reference)amountType.GetRelationship(PostgresRelationshipNames.Type)!.Entries.Single();
            Assert.Equal("numeric", amountRef.Name);
            Assert.Equal(12L, amountType.GetProperty<long?>(PostgresPropertyNames.Precision));
            Assert.Equal(2L, amountType.GetProperty<long?>(PostgresPropertyNames.Scale));
            Assert.Null(amountType.GetProperty<int?>(PostgresPropertyNames.Length));
        }
        finally
        {
            await db.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    private static Element TypeSpecOf(Model model, string columnName)
    {
        var table = model.Elements.Single(i => i.Type == PostgresElementTypes.SqlTable);
        var columns = table.GetRelationship(PostgresRelationshipNames.Columns)!;
        var column = columns.Entries.OfType<Element>().Single(c => c.Name == columnName);
        return (Element)column.GetRelationship(PostgresRelationshipNames.TypeSpecifier)!.Entries.Single();
    }

    private static string TypeSpecHash(Model model, string columnName)
        => Convert.ToHexString(TypeSpecOf(model, columnName).Hash);
}

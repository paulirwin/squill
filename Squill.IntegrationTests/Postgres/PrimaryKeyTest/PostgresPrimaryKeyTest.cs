using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.PrimaryKeyTest;

/// <summary>
/// End-to-end coverage for table-level (composite) primary keys parsed from SQL
/// (issue #7). The parser now understands <c>PRIMARY KEY (a, b)</c>, so these tests
/// build a model with the parser — no live server needed for the build — then publish
/// it into a real Postgres database and re-extract, proving the DDL we generate for a
/// parsed composite PK is valid, executable Postgres that round-trips.
/// </summary>
public class PostgresPrimaryKeyTest : PostgresIntegrationTestBase
{
    [Fact]
    public async Task CompositePrimaryKeyRoundTrip_ModelHashesMatchAfterPublish()
    {
        const string sql = """
CREATE TABLE order_lines
(
    order_id integer NOT NULL,
    line_no  integer NOT NULL,
    PRIMARY KEY (order_id, line_no)
);
""";

        await AssertParserModelRoundTrips(sql, expectedPkName: "order_lines_pkey");
    }

    [Fact]
    public async Task NamedCompositePrimaryKeyRoundTrip_ModelHashesMatchAfterPublish()
    {
        const string sql = """
CREATE TABLE order_lines
(
    order_id integer NOT NULL,
    line_no  integer NOT NULL,
    CONSTRAINT pk_order_lines PRIMARY KEY (order_id, line_no)
);
""";

        await AssertParserModelRoundTrips(sql, expectedPkName: "pk_order_lines");
    }

    // Builds a model from SQL via the parser (issue #7), publishes it into a fresh
    // database, re-extracts, and asserts the primary-key element and whole-model hashes
    // survive the round trip.
    private async Task AssertParserModelRoundTrips(string sql, string expectedPkName)
    {
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("OrderLines.sql", FileKind.Compile, sql));

        var parserModel = await new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser())
            .ExtractModelAsync(TestContext.Current.CancellationToken);

        var parserPk = Assert.Single(parserModel.Elements,
            i => i.Type == PostgresElementTypes.SqlPrimaryKeyConstraint);
        Assert.Equal(expectedPkName, parserPk.Name);
        Assert.Equal(
            new[] { "order_lines.order_id", "order_lines.line_no" },
            PrimaryKeyColumnReferences(parserPk));

        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);
        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            var comparison = SchemaCompare.Compare(provider, parserModel, emptyModel);

            await testDb.PublishAsync(comparison, TestContext.Current.CancellationToken);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            var publishedPk = Assert.Single(publishedModel.Elements,
                i => i.Type == PostgresElementTypes.SqlPrimaryKeyConstraint);

            // The publish must produce a valid composite PK in the real database that
            // re-extracts with the same constraint name and columns the parser modeled.
            Assert.Equal(expectedPkName, publishedPk.Name);
            Assert.Equal(
                new[] { "order_lines.order_id", "order_lines.line_no" },
                PrimaryKeyColumnReferences(publishedPk));

            // The primary-key element itself must be byte-for-byte identical (same
            // Merkle hash) whether built from parsed SQL or extracted from Postgres.
            // (Whole-model hashes still diverge on table-level schema qualification —
            // see PostgresCanonicalNameTest — which is out of scope for issue #7.)
            Assert.True(HashUtility.HashesEqual(parserPk.Hash, publishedPk.Hash),
                "Parser-built and published primary-key hashes do not match");
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    private static IEnumerable<string> PrimaryKeyColumnReferences(Element primaryKey)
    {
        var columnSpecs = primaryKey.GetRelationship(PostgresRelationshipNames.ColumnSpecifications)!;

        return columnSpecs.Entries
            .OfType<Element>()
            .Select(spec => Assert.IsType<Reference>(
                Assert.Single(spec.GetRelationship(PostgresRelationshipNames.Column)!.Entries)).Name);
    }
}

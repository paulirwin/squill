using Squill.Core;
using Squill.PostgresParser;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.CreateIndexTest;

public class PostgresCreateIndexTest : PostgresIntegrationTestBase
{
    private const string Fixture =
        "Squill.IntegrationTests.Postgres.CreateIndexTest.TableWithIndexes.sql";

    // Proves the CREATE INDEX DDL in the fixture is valid, executable Postgres:
    // TemporaryDatabaseModelBuilder runs the scripts against a real database and
    // would throw if any statement failed to execute.
    [Fact]
    public async Task CreateIndexSql_IsValidExecutablePostgres()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var modelBuilder = new TemporaryDatabaseModelBuilder(provider);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(Fixture, FileKind.Compile));

        // Extraction of the index itself is not yet implemented DB-side, but the
        // scripts must run without error to build the model at all.
        var model = await modelBuilder.BuildModelAsync(workspace, TestContext.Current.CancellationToken);

        var table = Assert.Single(model.Elements, i => i.Type == PostgresElementTypes.SqlTable);
        Assert.Equal("film", table.Name);
    }

    // Proves the parser-based model builder produces the expected SqlIndex model
    // from the same real-world fixture that Postgres accepts above.
    [Fact]
    public async Task ParserModelBuilder_ProducesSqlIndexElements()
    {
        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(Fixture, FileKind.Compile));

        var builder = new ParserWorkspaceModelBuilder(workspace, new AntlrPostgresParser());

        var model = (await builder.ExtractModelAsync(TestContext.Current.CancellationToken)).Model;

        var indexes = model.Elements
            .Where(i => i.Type == PostgresElementTypes.SqlIndex)
            .ToList();

        Assert.Equal(2, indexes.Count);

        var plain = Assert.Single(indexes, i => i.Name == "idx_film_title");
        Assert.Equal(false, plain.GetProperty<bool?>(PostgresPropertyNames.IsUnique));
        // A plain index with USING omitted defaults to btree, matching the DB builder.
        Assert.Equal("btree", plain.GetProperty<string>(PostgresPropertyNames.IndexMethod));

        var unique = Assert.Single(indexes, i => i.Name == "idx_film_title_unique");
        Assert.Equal(true, unique.GetProperty<bool?>(PostgresPropertyNames.IsUnique));
        Assert.Equal("btree", unique.GetProperty<string>(PostgresPropertyNames.IndexMethod));

        var uniqueColumnSpec = Assert.IsType<Element>(
            Assert.Single(unique.GetRelationship(PostgresRelationshipNames.ColumnSpecifications)!.Entries));
        Assert.Equal(false, uniqueColumnSpec.GetProperty<bool?>(PostgresPropertyNames.IsAscending));
        Assert.Equal(false, uniqueColumnSpec.GetProperty<bool?>(PostgresPropertyNames.NullsFirst));
    }
}

using Npgsql;
using Squill.Core;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.ConstraintIndexOptionTest;

/// <summary>
/// Full round trips for the index-shaped clauses on a PRIMARY KEY or UNIQUE constraint
/// (issue #210): INCLUDE and WITH (...) storage parameters. Each already worked on the
/// CREATE INDEX spelling and was silently dropped on the constraint spelling.
///
/// A round trip is the test that matters here. The unit tests prove the clauses reach the
/// model and are scripted, but only publishing to a real server and re-extracting proves the
/// declared constraint and the one the catalog reports back are the same object -- which is
/// what stops it re-diffing on every deploy.
/// </summary>
public class PostgresConstraintIndexOptionTest : PostgresIntegrationTestBase
{
    private async Task RoundTripAsync(string resource, Action<Model> assertModel)
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(resource, FileKind.Compile));

        var model = await new TemporaryDatabaseModelBuilder(provider)
            .BuildModelAsync(workspace, TestContext.Current.CancellationToken);

        assertModel(model);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, model, emptyModel),
                TestContext.Current.CancellationToken);

            var publishedModel = await dbModelBuilder.ExtractModelAsync(
                TestContext.Current.CancellationToken);

            Assert.True(
                HashUtility.HashesEqual(model.Hash, publishedModel.Hash),
                "Model hashes do not match after publish");

            // The re-deploy is the real proof: comparing the declared model against the model
            // just extracted from the target must produce nothing to do. A dropped or
            // mis-modeled clause shows up here as a constraint that redeploys forever.
            var reComparison = SchemaCompare.Compare(provider, model, publishedModel);

            Assert.Empty(reComparison.Deltas);
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ConstraintIncludeAndStorageParameters_RoundTrip()
    {
        await RoundTripAsync(
            "Squill.IntegrationTests.Postgres.ConstraintIndexOptionTest.TableWithConstraintInclude.sql",
            model =>
            {
                var pk = Assert.Single(model.Elements,
                    e => e.Type == PostgresElementTypes.SqlPrimaryKeyConstraint);
                var unique = Assert.Single(model.Elements,
                    e => e.Type == PostgresElementTypes.SqlUniqueConstraint);

                // The clauses survived the trip through a real server rather than being
                // dropped on the way, which is what this issue was about.
                Assert.NotNull(pk.GetRelationship(PostgresRelationshipNames.IncludedColumns));
                Assert.NotNull(unique.GetRelationship(PostgresRelationshipNames.IncludedColumns));
                Assert.Contains(
                    "fillfactor",
                    unique.GetProperty<string>(PostgresPropertyNames.StorageParameters)
                        ?? string.Empty,
                    StringComparison.Ordinal);
            });
    }

    /// <summary>
    /// An unnamed constraint's derived name folds in the INCLUDE columns
    /// (<c>UNIQUE (a, b) INCLUDE (c)</c> becomes <c>reservation_a_b_c_key</c>). Predicting it
    /// without them would name the element something the server never uses, so the round trip
    /// is what proves the prediction right.
    /// </summary>
    [Fact]
    public async Task UnnamedConstraintWithInclude_DerivedNameMatchesTheServer()
    {
        await RoundTripAsync(
            "Squill.IntegrationTests.Postgres.ConstraintIndexOptionTest.TableWithUnnamedIncludeConstraint.sql",
            model =>
            {
                var unique = Assert.Single(model.Elements,
                    e => e.Type == PostgresElementTypes.SqlUniqueConstraint);

                Assert.Equal("reservation_a_b_c_key", unique.Name);
            });
    }

    /// <summary>
    /// The whole point of issue #210 is that the two spellings of one declaration should agree.
    /// A UNIQUE constraint with INCLUDE and a unique index with INCLUDE describe the same
    /// backing index, so the server must report the same INCLUDE either way.
    /// </summary>
    [Fact]
    public async Task ConstraintIncludeMatchesTheEquivalentIndexSpelling()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using (var create = new NpgsqlCommand("""
            CREATE TABLE spelling_a (a integer, b integer, CONSTRAINT uq_a UNIQUE (a) INCLUDE (b));
            CREATE TABLE spelling_b (a integer, b integer);
            CREATE UNIQUE INDEX uq_b ON spelling_b (a) INCLUDE (b);
            """, connection))
        {
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var query = new NpgsqlCommand("""
            SELECT i.relname,
                   (SELECT count(*) FROM pg_attribute a
                     WHERE a.attrelid = i.oid AND a.attnum > ix.indnkeyatts) AS included
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            WHERE i.relname IN ('uq_a', 'uq_b')
            ORDER BY i.relname;
            """, connection);

        var included = new List<long>();

        await using (var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                included.Add(reader.GetInt64(1));
            }
        }

        // Both spellings produce an index with exactly one non-key (INCLUDE) column.
        Assert.Equal([1L, 1L], included);
    }
}

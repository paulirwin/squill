using Npgsql;
using Squill.Core;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.ExclusionConstraintTest;

/// <summary>
/// Full round trips for EXCLUDE constraints (issue #212), which threw
/// <c>NotImplementedException</c> before this and so could not be built at all.
///
/// A round trip is the test that matters. The unit tests prove the construct reaches the model
/// and is scripted, but only publishing to a real server and re-extracting proves the declared
/// constraint and the one the catalog reports back are the same object. Several facets here
/// are reported by the server whether or not they were written -- the access method always
/// comes back, an operator is reported unqualified when it resolves in <c>pg_catalog</c>, and
/// a predicate is rewritten -- so a facet modeled the wrong way re-diffs forever rather than
/// failing loudly.
/// </summary>
public class PostgresExclusionConstraintTest : PostgresIntegrationTestBase
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
            // just extracted from the target must produce nothing to do. A mis-modeled facet
            // shows up here as a constraint that redeploys forever.
            var reComparison = SchemaCompare.Compare(provider, model, publishedModel);

            Assert.Empty(reComparison.Deltas);
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    private static Element ExclusionOf(Model model)
        => Assert.Single(model.Elements,
            e => e.Type == PostgresElementTypes.SqlExclusionConstraint);

    private static IReadOnlyList<Element> ElementsOf(Element exclusion)
        => (exclusion.GetRelationship(PostgresRelationshipNames.ExclusionElements)?.Entries ?? [])
            .OfType<Element>()
            .Where(e => e.Type == PostgresElementTypes.SqlExclusionConstraintElement)
            .ToList();

    /// <summary>
    /// The canonical use of the feature: no two bookings may overlap in the same room. There
    /// is no other declarative way to express this in PostgreSQL.
    /// </summary>
    [Fact]
    public async Task GistExclusionConstraint_RoundTrips()
    {
        await RoundTripAsync(
            "Squill.IntegrationTests.Postgres.ExclusionConstraintTest.BookingWithExclusion.sql",
            model =>
            {
                var exclusion = ExclusionOf(model);

                Assert.Equal("no_overlap", exclusion.Name);
                Assert.Equal("gist",
                    exclusion.GetProperty<string>(PostgresPropertyNames.IndexMethod));

                var elements = ElementsOf(exclusion);

                Assert.Equal(2, elements.Count);
                Assert.Equal("=",
                    elements[0].GetProperty<string>(PostgresPropertyNames.ExclusionOperator));
                Assert.Equal("&&",
                    elements[1].GetProperty<string>(PostgresPropertyNames.ExclusionOperator));
            });
    }

    /// <summary>
    /// No USING clause and no constraint name, so both are supplied by the server. The model
    /// has to predict what it will choose: measured, an omitted access method comes back as
    /// <c>btree</c> and the derived name is <c>&lt;table&gt;_&lt;keys&gt;_excl</c>. Getting
    /// either wrong makes a bare EXCLUDE redeploy on every deploy.
    /// </summary>
    [Fact]
    public async Task BareExclusionConstraint_DefaultsMatchTheServer()
    {
        await RoundTripAsync(
            "Squill.IntegrationTests.Postgres.ExclusionConstraintTest.BareExclusion.sql",
            model =>
            {
                var exclusion = ExclusionOf(model);

                Assert.Equal("seat_section_row_no_excl", exclusion.Name);
                Assert.Equal("btree",
                    exclusion.GetProperty<string>(PostgresPropertyNames.IndexMethod));
            });
    }

    /// <summary>
    /// Every facet at once, including the ones that live on the backing index rather than on
    /// the constraint row (INCLUDE, storage parameters) and the predicate the server rewrites.
    /// </summary>
    [Fact]
    public async Task FullyDecoratedExclusionConstraint_RoundTrips()
    {
        await RoundTripAsync(
            "Squill.IntegrationTests.Postgres.ExclusionConstraintTest.FullExclusion.sql",
            model =>
            {
                var exclusion = ExclusionOf(model);

                Assert.Equal("no_double_booking", exclusion.Name);
                Assert.True(exclusion.GetProperty<bool?>(PostgresPropertyNames.IsDeferrable));
                Assert.True(
                    exclusion.GetProperty<bool?>(PostgresPropertyNames.IsInitiallyDeferred));
                Assert.NotNull(
                    exclusion.GetProperty<string>(PostgresPropertyNames.FilterPredicate));
                Assert.NotNull(
                    exclusion.GetRelationship(PostgresRelationshipNames.IncludedColumns));
                Assert.Contains(
                    "fillfactor",
                    exclusion.GetProperty<string>(PostgresPropertyNames.StorageParameters)
                        ?? string.Empty,
                    StringComparison.Ordinal);
            });
    }

    /// <summary>
    /// An expression key and a DESC ordering. The derived name takes the function's name
    /// (<c>account_lower_priority_excl</c>), and the ordering is filled in from indoption --
    /// measured, an exclusion constraint's backing index reports it exactly as an ordinary
    /// index does.
    /// </summary>
    [Fact]
    public async Task ExpressionKeyExclusionConstraint_RoundTrips()
    {
        await RoundTripAsync(
            "Squill.IntegrationTests.Postgres.ExclusionConstraintTest.ExpressionExclusion.sql",
            model =>
            {
                var exclusion = ExclusionOf(model);

                Assert.Equal("account_lower_priority_excl", exclusion.Name);
                Assert.Equal(2, ElementsOf(exclusion).Count);
            });
    }

    /// <summary>
    /// The constraint has to actually work once deployed, not merely round-trip: a second
    /// booking overlapping the first must be rejected by the server.
    /// </summary>
    [Fact]
    public async Task DeployedExclusionConstraint_RejectsAnOverlappingRow()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.ExclusionConstraintTest.BookingWithExclusion.sql",
            FileKind.Compile));

        var model = await new TemporaryDatabaseModelBuilder(provider)
            .BuildModelAsync(workspace, TestContext.Current.CancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            var emptyModel = await dbModelBuilder.ExtractModelAsync(
                TestContext.Current.CancellationToken);

            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, model, emptyModel),
                TestContext.Current.CancellationToken);

            var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                Database = testDb.Name,
            };

            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            await using (var insert = new NpgsqlCommand(
                """
                INSERT INTO booking (booking_id, room, during)
                VALUES (1, 7, tstzrange('2026-01-01', '2026-01-05'));
                """, connection))
            {
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            // A different room in the same window is fine: the constraint excludes a pair only
            // when EVERY element's operator holds, and room = 8 is not equal to room = 7.
            await using (var other = new NpgsqlCommand(
                """
                INSERT INTO booking (booking_id, room, during)
                VALUES (2, 8, tstzrange('2026-01-01', '2026-01-05'));
                """, connection))
            {
                await other.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            // The same room over an overlapping window is what the constraint forbids.
            await using var overlapping = new NpgsqlCommand(
                """
                INSERT INTO booking (booking_id, room, during)
                VALUES (3, 7, tstzrange('2026-01-03', '2026-01-08'));
                """, connection);

            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => overlapping.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

            Assert.Equal("23P01", exception.SqlState);
            Assert.Equal("no_overlap", exception.ConstraintName);
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }
}

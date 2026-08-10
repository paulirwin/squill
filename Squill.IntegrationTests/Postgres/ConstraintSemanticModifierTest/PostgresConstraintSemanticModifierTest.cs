using Npgsql;
using Squill.Core;
using Squill.Provider.Postgres;

namespace Squill.IntegrationTests.Postgres.ConstraintSemanticModifierTest;

/// <summary>
/// Full round trips for the constraint modifiers that parsed and were then discarded
/// (issue #205): <c>MATCH FULL</c> on a foreign key and <c>NO INHERIT</c> on a CHECK.
///
/// A round trip is what matters here. The unit tests prove the modifiers reach the model and
/// are scripted, but only publishing to a real server and re-extracting proves the declared
/// constraint and the one the catalog reports back are the same object. This suite also
/// asserts the modifiers change what the server actually <em>enforces</em>, which is the harm
/// the issue describes: a dropped MATCH FULL deploys a foreign key that accepts rows the
/// source intended to reject.
/// </summary>
public class PostgresConstraintSemanticModifierTest : PostgresIntegrationTestBase
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

            // The re-deploy is the real proof: a dropped or mis-modeled clause shows up here as
            // a constraint that redeploys forever.
            var reComparison = SchemaCompare.Compare(provider, model, publishedModel);

            Assert.Empty(reComparison.Deltas);
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task MatchFullForeignKey_RoundTrips()
    {
        await RoundTripAsync(
            "Squill.IntegrationTests.Postgres.ConstraintSemanticModifierTest.TableWithMatchFull.sql",
            model =>
            {
                var fk = Assert.Single(model.Elements,
                    e => e.Type == PostgresElementTypes.SqlForeignKeyConstraint);

                // MATCH FULL survived the trip through a real server, and the ON DELETE action
                // written after it was not swallowed on the way.
                Assert.Equal("Full", fk.GetProperty<string>(PostgresPropertyNames.MatchType));
                Assert.Equal("Cascade", fk.GetProperty<string>(PostgresPropertyNames.DeleteAction));
            });
    }

    [Fact]
    public async Task NoInheritCheck_RoundTrips()
    {
        await RoundTripAsync(
            "Squill.IntegrationTests.Postgres.ConstraintSemanticModifierTest.TableWithNoInheritCheck.sql",
            model =>
            {
                var noInherit = Assert.Single(model.Elements,
                    e => e.Type == PostgresElementTypes.SqlCheckConstraint
                         && e.Name as string == "ck_measurement_reading");
                var inherited = Assert.Single(model.Elements,
                    e => e.Type == PostgresElementTypes.SqlCheckConstraint
                         && e.Name as string == "ck_measurement_quality");

                Assert.True(noInherit.GetProperty<bool?>(PostgresPropertyNames.IsNoInherit));

                // The ordinary CHECK alongside it gains nothing, so existing projects do not
                // start re-diffing.
                Assert.Null(inherited.GetProperty<bool?>(PostgresPropertyNames.IsNoInherit));

                // pg_get_constraintdef renders NO INHERIT as a suffix outside the predicate's
                // parentheses; if it leaked into the expression the constraint would re-diff.
                Assert.DoesNotContain(
                    "NO INHERIT",
                    noInherit.GetProperty<string>(PostgresPropertyNames.CheckExpression) ?? string.Empty,
                    StringComparison.Ordinal);
            });
    }

    /// <summary>
    /// The harm the issue describes, measured rather than asserted about: a foreign key
    /// deployed without its MATCH FULL accepts a partially-NULL composite key that the declared
    /// constraint rejects. This runs against the schema Squill actually deploys, so it fails if
    /// the clause is dropped anywhere between parse and deploy.
    /// </summary>
    [Fact]
    public async Task DeployedMatchFullForeignKey_RejectsPartiallyNullKeys()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.ConstraintSemanticModifierTest.TableWithMatchFull.sql",
            FileKind.Compile));

        var model = await new TemporaryDatabaseModelBuilder(provider)
            .BuildModelAsync(workspace, TestContext.Current.CancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, model,
                    await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken)),
                TestContext.Current.CancellationToken);

            var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                Database = testDb.Name,
            };

            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            await using (var seed = new NpgsqlCommand(
                "INSERT INTO tenant (tenant_id, region_id) VALUES (1, 1);", connection))
            {
                await seed.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            // Mixing a non-NULL and a NULL is exactly what MATCH FULL forbids and MATCH SIMPLE
            // permits, so this insert is the difference between the two.
            await using var insert = new NpgsqlCommand(
                "INSERT INTO assignment (id, tenant_id, region_id) VALUES (1, 1, NULL);",
                connection);

            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

            // 23503 is foreign_key_violation. Under MATCH SIMPLE -- what a dropped clause would
            // have deployed -- this row is accepted and no exception is raised at all, so the
            // rejection itself is the assertion. (The server names MATCH FULL as the reason in
            // Detail, which Npgsql redacts unless the connection string opts in, so the error
            // code and the constraint it names are what is checked here.)
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
            Assert.Equal("fk_assignment_tenant", exception.ConstraintName);
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// The counterpart for NO INHERIT: a child table must not receive the constraint. If the
    /// clause were dropped, the child would inherit it and reject rows the source allows.
    /// </summary>
    [Fact]
    public async Task DeployedNoInheritCheck_IsNotInheritedByAChildTable()
    {
        IDatabaseProvider provider = new PostgresDatabaseProvider(ConnectionString);

        var workspace = new Workspace();
        workspace.Files.Add(new EmbeddedResourceFile(
            "Squill.IntegrationTests.Postgres.ConstraintSemanticModifierTest.TableWithNoInheritCheck.sql",
            FileKind.Compile));

        var model = await new TemporaryDatabaseModelBuilder(provider)
            .BuildModelAsync(workspace, TestContext.Current.CancellationToken);

        var testDb = await provider.CreateDatabaseAsync(
            $"squill_test_{Guid.NewGuid():n}", TestContext.Current.CancellationToken);
        var dbModelBuilder = provider.CreateDatabaseModelBuilder(testDb);

        try
        {
            await testDb.PublishAsync(
                SchemaCompare.Compare(provider, model,
                    await dbModelBuilder.ExtractModelAsync(TestContext.Current.CancellationToken)),
                TestContext.Current.CancellationToken);

            var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
            {
                Database = testDb.Name,
            };

            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(TestContext.Current.CancellationToken);

            await using (var child = new NpgsqlCommand(
                "CREATE TABLE measurement_child () INHERITS (measurement);", connection))
            {
                await child.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using var query = new NpgsqlCommand("""
                SELECT c.conname
                FROM pg_constraint c
                WHERE c.conrelid = 'measurement_child'::regclass
                  AND c.contype = 'c'
                ORDER BY c.conname;
                """, connection);

            var inheritedNames = new List<string>();

            await using (var reader = await query.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            {
                while (await reader.ReadAsync(TestContext.Current.CancellationToken))
                {
                    inheritedNames.Add(reader.GetString(0));
                }
            }

            // Only the ordinary CHECK is inherited; the NO INHERIT one stops at the parent.
            Assert.Equal(["ck_measurement_quality"], inheritedNames);
        }
        finally
        {
            await testDb.DropAsync(TestContext.Current.CancellationToken);
        }
    }
}
